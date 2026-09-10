using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class TelemetryIntegration : IDisposable
{
    internal readonly CompatibilityInfo Compatibility = new CompatibilityInfo();
    internal readonly Dictionary<string, FeatureStatus> Features = new Dictionary<string, FeatureStatus>();
    private readonly Dictionary<string, MethodInfo> targets = new Dictionary<string, MethodInfo>();
    private readonly Harmony harmony = new Harmony(Plugin.PluginGuid);
    private readonly Action<string> log;
    private double nextAudit;
    internal bool GameSupported => Compatibility.status == "supported";

    internal TelemetryIntegration(ConfigFile config, Action<string> log)
    {
        this.log = log;
        try
        {
            var version = typeof(ZNet).Assembly.GetType("Version", true);
            // Reflection reads the running assembly, avoiding an inlined build-time constant.
            Compatibility.gameVersion = version.GetProperty("CurrentVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString();
            Compatibility.networkVersion = Convert.ToUInt32(version.GetField("c_networkVersion", BindingFlags.Public | BindingFlags.Static).GetRawConstantValue());
            Compatibility.gameModuleId = typeof(ZNet).Module.ModuleVersionId.ToString();
            Compatibility.status = CompatibilityPolicy.Evaluate(Compatibility.gameVersion, Compatibility.networkVersion);
        }
        catch (Exception ex) { Compatibility.status = "inspection_failed:" + ex.GetType().Name; }
        log($"Compatibility: {Compatibility.status}; game={Compatibility.gameVersion}; protocol={Compatibility.networkVersion}; module={Compatibility.gameModuleId}");
        foreach (string id in new[] { "FrameTiming", "GameCounters", "NetworkTiming", "ZdoReceive", "SteamTransport", "Ownership", "ConnectionHealth", "ProcessResources" })
        {
            bool enabled = config.Bind("Features", id, true, "Enable this diagnostic probe. Restart required; no gameplay tuning.").Value;
            Features[id] = new FeatureStatus { id = id, enabled = enabled,
                status = !enabled ? "configured_disabled" : id != "FrameTiming" && id != "ProcessResources" && !GameSupported ? "blocked_compatibility" : "available" };
        }
        if (Features["SteamTransport"].Collect)
        {
            try
            {
                Compatibility.steamInterface = SteamMetrics.Initialize();
                Features["SteamTransport"].target = Compatibility.steamInterface + ".GetConnectionRealTimeStatus";
                Features["SteamTransport"].detail = "Contract verified; awaiting a connected Steam peer";
            }
            catch (Exception ex) { Fail("SteamTransport", "contract_mismatch", ex.Message); }
        }
        if (Features["Ownership"].Collect)
        {
            var field = AccessTools.DeclaredField(typeof(ZNetScene), "m_instances");
            if (field == null || field.IsStatic || field.FieldType != typeof(Dictionary<ZDO, ZNetView>))
                Fail("Ownership", "contract_mismatch", "Expected instance Dictionary<ZDO, ZNetView> m_instances");
        }
    }

    internal void Install()
    {
        Install("NetworkTiming", "Update", CompatibilityPolicy.UpdateHash, new[] { typeof(float) });
        Install("ZdoReceive", "RPC_ZDOData", CompatibilityPolicy.ReceiveHash, new[] { typeof(ZRpc), typeof(ZPackage) });
        foreach (var feature in Features.Values) log($"Feature {feature.id}: {feature.status}; target={feature.target}; {feature.detail}");
    }

    private void Install(string id, string name, string fingerprint, Type[] args)
    {
        var feature = Features[id];
        feature.target = "ZDOMan." + name;
        if (!feature.Collect) return;
        MethodInfo method = null;
        try
        {
            method = AccessTools.DeclaredMethod(typeof(ZDOMan), name, args);
            if (!CompatibilityPolicy.Signature(method, typeof(ZDOMan), typeof(void), args))
            { Fail(id, "signature_mismatch", "Target signature does not match"); return; }
            string actual = CompatibilityPolicy.Fingerprint(method);
            if (!CompatibilityPolicy.MatchesFingerprint(fingerprint, actual))
            { Fail(id, "fingerprint_mismatch", "Unreviewed target IL SHA-256: " + actual); return; }
            var foreign = ForeignOwners(method);
            if (foreign.Length != 0) { Fail(id, "conflicting_patch", string.Join(", ", foreign)); return; }
            targets[id] = method;
            if (id == "NetworkTiming")
                harmony.Patch(method, prefix: Hook(nameof(TelemetryHooks.BeginNetworkUpdate)), finalizer: Hook(nameof(TelemetryHooks.EndNetworkUpdate)));
            else harmony.Patch(method, postfix: Hook(nameof(TelemetryHooks.AfterZdoData)));
            if (!Installed(id, method)) throw new InvalidOperationException("Harmony patch registration does not match the expected hooks");
            feature.status = "installed_waiting";
            feature.detail = "Contract verified; no callback observed yet";
        }
        catch (Exception ex)
        {
            if (method != null) harmony.Unpatch(method, HarmonyPatchType.All, Plugin.PluginGuid);
            Fail(id, "installation_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(TelemetryHooks), name);
    private static string[] ForeignOwners(MethodInfo method) => Harmony.GetPatchInfo(method)?.Owners
        .Where(owner => owner != Plugin.PluginGuid).Distinct().ToArray() ?? new string[0];
    private static bool Match(IEnumerable<Patch> patches, string name) => patches.Count(p => p.owner == Plugin.PluginGuid
        && p.PatchMethod == AccessTools.Method(typeof(TelemetryHooks), name)) == 1;
    private static bool Installed(string id, MethodInfo method)
    {
        var info = Harmony.GetPatchInfo(method);
        int ownCount = info == null ? 0 : info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers).Count(p => p.owner == Plugin.PluginGuid);
        return info != null && ownCount == (id == "NetworkTiming" ? 2 : 1) && (id == "NetworkTiming"
            ? Match(info.Prefixes, nameof(TelemetryHooks.BeginNetworkUpdate)) && Match(info.Finalizers, nameof(TelemetryHooks.EndNetworkUpdate))
            : Match(info.Postfixes, nameof(TelemetryHooks.AfterZdoData)));
    }

    internal void Audit(double now)
    {
        if (now < nextAudit) return;
        nextAudit = now + 5;
        foreach (var target in targets)
        {
            var feature = Features[target.Key];
            if (!feature.Collect)
            {
                harmony.Unpatch(target.Value, HarmonyPatchType.All, Plugin.PluginGuid);
                continue;
            }
            var foreign = ForeignOwners(target.Value);
            if (foreign.Length == 0 && Installed(target.Key, target.Value)) continue;
            harmony.Unpatch(target.Value, HarmonyPatchType.All, Plugin.PluginGuid);
            Fail(target.Key, "patch_changed", "Hook removed or overlapping owner appeared: " + string.Join(", ", foreign));
        }
    }

    internal void Observe(string id, Action capture)
    {
        var feature = Features[id];
        if (!feature.Collect) return;
        try { capture(); feature.invocations++; feature.status = "active"; feature.detail = null; }
        catch (Exception ex) { Fail(id, "probe_failed", ex.GetType().Name); }
    }

    private void Fail(string id, string status, string detail)
    {
        Features[id].status = status;
        Features[id].detail = detail;
        log($"Feature {id}: {status}; {detail}");
    }

    internal FeatureStatus[] Snapshot() => Features.Values.Select(f => f.Copy()).ToArray();
    public void Dispose() => harmony.UnpatchSelf();
}
