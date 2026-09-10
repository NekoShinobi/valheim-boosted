using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ValheimBoosted;

// Minimal game/config fixtures. Patch lifecycle uses the actual shipped Harmony DLL.
public static class Version { public static System.Version CurrentVersion => new System.Version(1, 0, 7); public const uint c_networkVersion = 39; }
public sealed class ZNet {
    public static ZNet instance;
    public bool Dedicated = true;
    public readonly List<ZNetPeer> Peers = new List<ZNetPeer>();
    public List<ZNetPeer> GetPeers() => Peers;
    public ZNetPeer GetPeer(long uid) => Peers.Find(p => p.m_uid == uid);
    public bool IsDedicated() => Dedicated;
    public static implicit operator bool(ZNet value) => value != null;
}
public class TestSocket { public bool Connected = true; public bool IsConnected() => Connected; public void Close() => Connected = false; }
public sealed class ZSteamSocket : TestSocket { }
public sealed class ZNetPeer { public long m_uid = 1; public ZRpc m_rpc = new ZRpc(); public TestSocket m_socket = new ZSteamSocket(); public bool Ready = true; public bool IsReady() => Ready; }
public sealed class ZRpc {
    public readonly List<Tuple<string, object[]>> Sent = new List<Tuple<string, object[]>>();
    public readonly Dictionary<string, Action<ZRpc, ZPackage>> Handlers = new Dictionary<string, Action<ZRpc, ZPackage>>();
    public void Register<T>(string name, Action<ZRpc, T> handler) => Handlers[name] = (rpc,p) => handler(rpc,(T)(object)p);
    public void Unregister(string name) => Handlers.Remove(name);
    public void Invoke(string name, params object[] args) => Sent.Add(Tuple.Create(name,args));
    public void Deliver(string name, byte[] bytes) => Handlers[name](this,new ZPackage(bytes));
}
public sealed class ZPackage {
    private readonly byte[] bytes;
    public ZPackage(byte[] bytes) { this.bytes = bytes; }
    public int Size() => bytes.Length;
    public byte[] GetArray() => bytes;
}
public sealed class ZDO { }
public sealed class ZNetView { }
public sealed class ZNetScene { private Dictionary<ZDO, ZNetView> m_instances; }
public sealed class ZDOMan {
    public static ZDOMan instance; public byte[] Received; public int ReceiveCalls;
    public float Total;
    public sealed class ZDOPeer { public ZNetPeer m_peer; }
    public readonly List<ZDOPeer> Peers = new List<ZDOPeer>();
    public int Sent, Cursor = -1, VanillaCalls, Sends;
    public float Timer;
    public bool ThrowSend;
    public Action AfterSend;
    [MethodImpl(MethodImplOptions.NoInlining)] public bool SendZDOs(ZDOPeer peer, bool flush) { Sends++; if (ThrowSend) throw new InvalidOperationException("send failed"); Sent++; AfterSend?.Invoke(); return true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void SendZDOToPeers2(float dt) { VanillaCalls++; }

    [MethodImpl(MethodImplOptions.NoInlining)] public void Update(float delta) { if (delta < 0) throw new InvalidOperationException("original"); Total += delta; }
    [MethodImpl(MethodImplOptions.NoInlining)] private void RPC_ZDOData(ZRpc rpc, ZPackage package) { Received = package.GetArray(); ReceiveCalls++; }
}
namespace BepInEx.Configuration {
    public sealed class ConfigDescription { public ConfigDescription(string text, object range) { } }
    public sealed class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile {
        public bool Enabled = true;
        public bool? Scheduling;
        public HashSet<string> Disabled = new HashSet<string>();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) => new ConfigEntry<T> { Value = typeof(T) == typeof(bool) ? (T)(object)(section == "Scheduling" ? (Scheduling ?? (bool)(object)value) : section == "Features" ? Enabled && !Disabled.Contains(key) : (bool)(object)value) : value };
        public ConfigEntry<T> Bind<T>(string section, string key, T value, ConfigDescription description) => new ConfigEntry<T> { Value = value };
    }
}
namespace ValheimBoosted {
    public static class Plugin { public const string PluginVersion = "test"; public const string PluginGuid = "valheim.boosted.integration-tests"; }
    internal static class SteamMetrics {
        internal static string Initialize() => "fixture";
        internal static void ReadManagedQueue(ZSteamSocket socket, PeerMetrics sample) => sample.applicationQueuedBytes = 0;
        internal static void Read(ZSteamSocket socket, PeerMetrics sample) { sample.measurementStatus = "available"; sample.rttMs = 200; sample.estimatedTransportQueueMs = 0; sample.pendingReliableBytes = 0; sample.estimatedSendRateBytesPerSecond = 153600; }
    }
    internal sealed class SteamRateAdapter {
        internal string Backend => "fixture";
        internal SteamRateSetting Read(ZSteamSocket socket, bool max) => new SteamRateSetting { Value = 153600, Inherited = true };
        internal void WriteMaximum(ZSteamSocket socket, int? value) { }
    }
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
        SchedulerIntegrationChecks.Run(Check);
        CompressionIntegrationChecks.Run(Check);
        Console.WriteLine($"PASS: {checks} Harmony integration checks."); return 0;
    }
}
