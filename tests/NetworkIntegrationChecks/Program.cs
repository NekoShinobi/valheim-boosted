using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using ValheimBoosted;

internal static class Program
{
    private static int checks;
    private static readonly List<byte> replayed = new List<byte>();
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static bool Receive(ZPackage __1) { replayed.Add(__1.GetArray()[0]); return false; }
    private static void Overlap() { }
    private static bool forced;
    private static bool Visibility(ZNetPeer peer) => forced || peer.m_publicRefPos;
    private static IEnumerable<CodeInstruction> VisibilityTransform(IEnumerable<CodeInstruction> instructions)
        => NetworkEnhancementTransforms.ForceSharing(instructions, AccessTools.Method(typeof(Program), nameof(Visibility)));
    private sealed class VisibilityFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)] internal bool Read(ZNetPeer peer) => peer.m_publicRefPos;
    }
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass game Managed directory");
        var directory = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var name = new AssemblyName(e.Name).Name;
            if (name == "mscorlib" || name == "netstandard" || name == "System" || name.StartsWith("System.")) return null;
            var path = Path.Combine(directory, name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        // Check before Run is JIT-compiled: loading ZNet's fields requires the runtime HTTP assembly.
        // Resolve Mono's implementation, never a framework DLL copied from the game's Managed directory.
        if (Type.GetType("System.Net.Http.HttpClient, System.Net.Http, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false) == null)
        {
            System.Console.Error.WriteLine("Missing Mono System.Net.Http runtime library. On Ubuntu/Debian, install libmono-system-net-http4.0-cil and rerun the network integration checks.");
            return 1;
        }
        AccessTools.PropertySetter(typeof(BepInEx.Paths), "BepInExConfigPath").Invoke(null, new object[] { Path.Combine(Path.GetTempPath(), "vb-network-checks-bepinex.cfg") });
        return Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        NetworkEnhancementChecks.Run(Check);
        string directory = Path.Combine(Path.GetTempPath(), "vb-network-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigFile(Path.Combine(directory, "defaults.cfg"), false) { SaveOnConfigSet = false };
            using (var telemetry = new TelemetryIntegration(config, _ => { }))
            using (var relevance = new ReplicationRelevance(config, telemetry, _ => { }))
            using (var early = new EarlyZdoIntegration(config, telemetry, _ => { }))
            using (var map = new MapSharingIntegration(config, telemetry, relevance, _ => { }))
            {
                foreach (string id in new[] { "FreshInterest", "ActorPriority", "EarlyZdoBuffer", "ForceMapSharing", "MapUpdates" })
                    Check(telemetry.Features[id].enabled && telemetry.Features[id].status == "installed_waiting", id + " default-on, exact contract and real Harmony installation");
                Check(config["Map", "ForceLocationSharing"].BoxedValue.Equals(true), "Generated configuration forces location sharing by default");
                EarlyReplay(early);
                var other = new Harmony("network-checks.other");
                var target = NetworkEnhancementContracts.SyncList();
                try
                {
                    other.Patch(target, postfix: new HarmonyMethod(typeof(Program), nameof(Overlap)));
                    relevance.Audit();
                    Check(!telemetry.Features["FreshInterest"].Collect && telemetry.Features["ActorPriority"].Collect, "Conflicting interest patch disables only that feature");
                    Check(Harmony.GetPatchInfo(target).Owners.Contains(other.Id), "Foreign patch preserved");
                }
                finally { other.UnpatchSelf(); }
            }
            var disabled = new ConfigFile(Path.Combine(directory, "disabled.cfg"), false) { SaveOnConfigSet = false };
            foreach (var key in new[] { "FreshInterest", "ActorPriority", "EarlyZdoBuffer" }) disabled.Bind("Replication", key, false, "test");
            disabled.Bind("Map", "ForceLocationSharing", false, "test"); disabled.Bind("Map", "FastUpdates", false, "test");
            using (var telemetry = new TelemetryIntegration(disabled, _ => { }))
            using (var relevance = new ReplicationRelevance(disabled, telemetry, _ => { }))
            using (var early = new EarlyZdoIntegration(disabled, telemetry, _ => { }))
            using (var map = new MapSharingIntegration(disabled, telemetry, relevance, _ => { }))
                foreach (string id in new[] { "FreshInterest", "ActorPriority", "EarlyZdoBuffer", "ForceMapSharing", "MapUpdates" })
                    Check(telemetry.Features[id].status == "configured_disabled", id + " respects saved opt-out");
            var h = new Harmony("network-checks.visibility");
            try
            {
                h.Patch(AccessTools.Method(typeof(VisibilityFixture), "Read"), transpiler: new HarmonyMethod(typeof(Program), nameof(VisibilityTransform)));
                var peer = new ZNetPeer(new Socket(), false); var fixture = new VisibilityFixture();
                forced = true; Check(fixture.Read(peer) && !peer.m_publicRefPos, "Server publishes private player's location without modifying their preference");
                forced = false; Check(!fixture.Read(peer), "Disabling server policy restores voluntary visibility");
                peer.m_publicRefPos = true; Check(fixture.Read(peer), "Voluntary sharing preserved");
                bool refused = false; try { VisibilityTransform(Array.Empty<CodeInstruction>()).ToArray(); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "Changed visibility IL refuses patch installation");
            }
            finally { h.UnpatchSelf(); }
            System.Console.WriteLine("PASS: " + checks + " network policy, actual game patch, connection FIFO and visibility checks");
            return 0;
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void EarlyReplay(EarlyZdoIntegration early)
    {
        var net = (ZNet)FormatterServices.GetUninitializedObject(typeof(ZNet));
        var live = new List<ZNetPeer>(); AccessTools.Field(typeof(ZNet), "m_peers").SetValue(net, live);
        AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(null, false); AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, net);
        var manager = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
        var peerField = AccessTools.Field(typeof(ZDOMan), "m_peers"); peerField.SetValue(manager, Activator.CreateInstance(peerField.FieldType));
        AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, manager);
        var capture = new Harmony(Plugin.PluginGuid);
        try
        {
            capture.Patch(NetworkEnhancementContracts.Receive(), prefix: new HarmonyMethod(typeof(Program), nameof(Receive)));
            var socket = new Socket(); var peer = new ZNetPeer(socket, true); live.Add(peer);
            // Call the installed setup hook without initiating Steam or the game's password handshake.
            AccessTools.Method(typeof(EarlyZdoIntegration), "Connected").Invoke(null, new object[] { net, peer });
            Deliver(peer, 1); Deliver(peer, 2);
            Check(early.Buffered == 2 && replayed.Count == 0, "Packets arriving before AddPeer are retained");
            peer.m_uid = 123; manager.AddPeer(peer); // The real patched game method replaces its RPC registration.
            Deliver(peer, 3);
            Check(early.BufferCompressed(peer.m_rpc, new ZPackage(new byte[] { 4 })), "Decoded compression frames join the same early FIFO");
            Check(replayed.Count == 0 && early.QueuedBytes == 4, "Live and compressed frames cannot jump the replay queue");
            for (int i = 0; i < 10 && early.QueuedBytes > 0; i++) early.Tick(TelemetryCollector.Now);
            Check(replayed.SequenceEqual(new byte[] { 1, 2, 3, 4 }) && early.Replayed == 4 && early.QueuedBytes == 0, "Bounded replay invokes vanilla receiver exactly once in order");
            Deliver(peer, 5); Check(replayed.Last() == 5 && early.Buffered == 4, "After drain, vanilla handler restored");
            live.Clear();
            var second = new ZNetPeer(new Socket(), true); live.Add(second);
            AccessTools.Method(typeof(EarlyZdoIntegration), "Connected").Invoke(null, new object[] { net, second });
            for (int i = 0; i <= EarlyZdoQueue.MaxPackets; i++) Deliver(second, 9);
            Check(!second.m_socket.IsConnected() && early.Failures == 1 && early.QueuedBytes == 0, "Overflow closes connection and releases all buffered state");
        }
        finally
        {
            capture.UnpatchSelf(); live.Clear(); early.Tick(TelemetryCollector.Now);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, null); AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, null);
        }
    }
    private static void Deliver(ZNetPeer peer, byte value)
    {
        var table = (IDictionary)AccessTools.Field(typeof(ZRpc), "m_functions").GetValue(peer.m_rpc);
        var handler = table["ZDOData".GetStableHashCode()];
        var action = (Action<ZRpc, ZPackage>)AccessTools.Field(handler.GetType(), "m_action").GetValue(handler);
        action(peer.m_rpc, new ZPackage(new byte[] { value }));
    }
    private sealed class Socket : ISocket
    {
        private bool connected = true;
        public bool IsConnected() => connected; public void Close() => connected = false; public void Dispose() => Close();
        public void Send(ZPackage pkg) { } public ZPackage Recv() => null; public int GetSendQueueSize() => 0; public int GetCurrentSendRate() => 0;
        public bool IsHost() => false; public bool GotNewData() => false; public string GetEndPointString() => "fixture";
        public void GetAndResetStats(out int sent, out int received) { sent = received = 0; }
        public void GetConnectionQuality(out float local, out float remote, out int ping, out float outgoing, out float incoming) { local = remote = outgoing = incoming = 0; ping = 0; }
        public ISocket Accept() => null; public int GetHostPort() => 0; public bool Flush() => true; public string GetHostName() => "fixture"; public void VersionMatch() { }
    }
}
