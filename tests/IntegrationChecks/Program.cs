using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ValheimBoosted;

// Minimal game/config fixtures. Patch lifecycle uses the actual shipped Harmony DLL.
public static class Version { public static System.Version CurrentVersion => new System.Version(1, 0, 7); public const uint c_networkVersion = 39; }
public sealed class ZNet { }
public sealed class ZRpc { }
public sealed class ZPackage { }
public sealed class ZDO { }
public sealed class ZNetView { }
public sealed class ZNetScene { private Dictionary<ZDO, ZNetView> m_instances; }
public sealed class ZDOMan {
    public float Total;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Update(float delta) { if (delta < 0) throw new InvalidOperationException("original"); Total += delta; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_ZDOData(ZRpc rpc, ZPackage package) { }
}
namespace BepInEx.Configuration {
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile {
        public bool Enabled = true;
        public HashSet<string> Disabled = new HashSet<string>();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) => new ConfigEntry<T> { Value = (T)(object)(Enabled && !Disabled.Contains(key)) };
    }
}
namespace ValheimBoosted {
    public static class Plugin { public const string PluginGuid = "valheim.boosted.integration-tests"; }
    internal static class SteamMetrics { internal static string Initialize() => "fixture"; }
    internal static class TelemetryHooks {
        internal static TelemetryIntegration Integration;
        internal static void BeginNetworkUpdate() { }
        internal static Exception EndNetworkUpdate(Exception __exception) { Integration?.Observe("NetworkTiming", () => { }); return __exception; }
        internal static void AfterZdoData() { }
    }
}
internal static class Program {
    static int checks;
    static void Check(bool value, string message) { if(!value) throw new Exception(message); checks++; }
    static void Overlap() { }
    static void InstallReviewedFixture(TelemetryIntegration integration) {
        typeof(TelemetryIntegration).GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(string), typeof(string), typeof(string), typeof(Type[]) }, null).Invoke(integration,
            new object[] { "NetworkTiming", "Update", CompatibilityPolicy.Fingerprint(typeof(ZDOMan).GetMethod("Update")), new[] { typeof(float) } });
    }
    static int Main() {
        var method = typeof(ZDOMan).GetMethod("Update");
        using(var disabled = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile { Enabled = false }, _ => { })) {
            disabled.Install();
            Check(Harmony.GetPatchInfo(method) == null, "Disabled features install no patches");
            var game = new ZDOMan(); game.Update(3); Check(game.Total == 3, "Disabled path retains original behavior");
        }
        using(var selective = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile { Disabled = new HashSet<string> { "NetworkTiming" } }, _ => { })) {
            InstallReviewedFixture(selective);
            Check(selective.Features["NetworkTiming"].status == "configured_disabled" && selective.Features["SteamTransport"].Collect, "Probe switches are independent");
        }
        var preexisting = new Harmony("preexisting.integration-test");
        try {
            preexisting.Patch(method, prefix: new HarmonyMethod(typeof(Program).GetMethod("Overlap", BindingFlags.NonPublic | BindingFlags.Static)));
            using(var overlap = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile(), _ => { })) {
                InstallReviewedFixture(overlap);
                Check(overlap.Features["NetworkTiming"].status == "conflicting_patch", "Initial overlapping owner blocks installation");
                Check(!Harmony.GetPatchInfo(method).Owners.Contains(Plugin.PluginGuid), "Initial overlap leaves foreign patch untouched");
            }
        } finally { preexisting.UnpatchSelf(); }
        using(var wrong = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile(), _ => { })) {
            wrong.Install();
            Check(wrong.Features["NetworkTiming"].status == "fingerprint_mismatch", "Changed game body rejected");
            Check(Harmony.GetPatchInfo(method) == null || !Harmony.GetPatchInfo(method).Owners.Contains(Plugin.PluginGuid), "Rejected contract leaves no patch");
        }
        using(var integration = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile(), _ => { })) {
            TelemetryHooks.Integration = integration;
            InstallReviewedFixture(integration);
            Check(integration.Features["NetworkTiming"].status == "installed_waiting", "Installation is not reported as execution");
            var game = new ZDOMan(); game.Update(4);
            Check(game.Total == 4 && integration.Features["NetworkTiming"].invocations == 1, "Instrumentation preserves behavior and counts callbacks");
            try { game.Update(-1); throw new Exception("Original exception suppressed"); }
            catch(InvalidOperationException e) { Check(e.Message == "original", "Original exception preserved"); }
            var other = new Harmony("other.integration-test");
            try {
                other.Patch(method, prefix: new HarmonyMethod(typeof(Program).GetMethod("Overlap", BindingFlags.NonPublic | BindingFlags.Static)));
                integration.Audit(10);
                Check(integration.Features["NetworkTiming"].status == "patch_changed", "Late conflicting patch disables probe");
                Check(!Harmony.GetPatchInfo(method).Owners.Contains(Plugin.PluginGuid), "Only own hooks removed");
                Check(Harmony.GetPatchInfo(method).Owners.Contains("other.integration-test"), "Other mod patch preserved");
            } finally { other.UnpatchSelf(); }
        }
        Check(Harmony.GetPatchInfo(method) == null || !Harmony.GetPatchInfo(method).Owners.Contains(Plugin.PluginGuid), "Dispose removes owned patches");
        using(var failed = new TelemetryIntegration(new BepInEx.Configuration.ConfigFile(), _ => { })) {
            TelemetryHooks.Integration = failed;
            InstallReviewedFixture(failed);
            failed.Observe("NetworkTiming", () => throw new Exception("probe error"));
            Check(failed.Features["NetworkTiming"].status == "probe_failed", "Callback failure is contained");
            failed.Audit(20);
            Check(!Harmony.GetPatchInfo(method).Owners.Contains(Plugin.PluginGuid), "Failed probe hooks removed at next audit");
        }
        Console.WriteLine($"PASS: {checks} Harmony integration checks."); return 0;
    }
}
