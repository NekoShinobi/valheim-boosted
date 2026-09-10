using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimBoosted;

// Each feature owns and audits its own hooks; one incompatibility cannot disable unrelated features.
internal sealed class NetworkFeaturePatch : IDisposable
{
    internal readonly FeatureStatus Status;
    private readonly Harmony harmony;
    private readonly Action<string> log;
    private readonly Dictionary<MethodInfo, MethodInfo[]> hooks = new Dictionary<MethodInfo, MethodInfo[]>();
    private readonly Dictionary<MethodInfo, string[]> dependencies = new Dictionary<MethodInfo, string[]>();
    internal NetworkFeaturePatch(string id, string section, string key, string description, ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        this.log = log; harmony = new Harmony(Plugin.PluginGuid + "." + id);
        bool enabled = config.Bind(section, key, true, description + " Restart required.").Value;
        Status = new FeatureStatus { id = id, enabled = enabled,
            status = !enabled ? "configured_disabled" : integration.GameSupported ? "available" : "blocked_compatibility" };
        integration.Features[id] = Status;
    }
    internal void Dependency(MethodInfo method, params string[] allowedOwners)
    { dependencies[method] = allowedOwners; CheckOwners(method, allowedOwners); }
    private void CheckOwners(MethodInfo method, string[] allowed)
    {
        if (Harmony.GetPatchInfo(method)?.Owners.Any(o => o != harmony.Id && !allowed.Contains(o)) == true)
            throw new InvalidOperationException("Overlapping patch: " + method.DeclaringType.Name + "." + method.Name);
    }
    internal void Patch(MethodInfo target, MethodInfo hook, bool transpiler = false)
    {
        Dependency(target);
        harmony.Patch(target, postfix: transpiler ? null : new HarmonyMethod(hook), transpiler: transpiler ? new HarmonyMethod(hook) : null);
        hooks[target] = new[] { hook };
        Status.target = string.Join(", ", hooks.Keys.Select(m => m.DeclaringType.Name + "." + m.Name));
        Status.status = "installed_waiting";
        Audit();
    }
    internal void Audit()
    {
        foreach (var entry in dependencies) CheckOwners(entry.Key, entry.Value);
        foreach (var entry in hooks)
        {
            var info = Harmony.GetPatchInfo(entry.Key);
            var own = info?.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Where(p => p.owner == harmony.Id).ToArray();
            if (own == null || own.Length != entry.Value.Length || own.Any(p => !entry.Value.Contains(p.PatchMethod)))
                throw new InvalidOperationException("Network hook registration changed: " + entry.Key.Name);
        }
    }
    internal void Fail(Exception ex)
    { Status.status = "runtime_failed"; Status.detail = ex.GetBaseException().Message; harmony.UnpatchSelf(); log(Status.id + " vanilla fallback: " + Status.detail); }
    internal void Used() { Status.invocations++; Status.status = "active"; }
    public void Dispose() => harmony.UnpatchSelf();
}
