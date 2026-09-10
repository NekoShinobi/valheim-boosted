using System;
using System.Collections.Generic;
using System.IO;
using ValheimBoosted;

// A queued two-ended RPC fixture exercises the production optional telemetry protocol.
// The game assemblies and native Steam transport are checked separately.
public sealed class ZPackage
{
    private readonly byte[] data;
    public ZPackage(byte[] bytes) { data = bytes; }
    public byte[] GetArray() => (byte[])data.Clone();
    public int Size() => data.Length;
}
public sealed class ZRpc
{
    private readonly Dictionary<string, Action<ZRpc, ZPackage>> handlers = new Dictionary<string, Action<ZRpc, ZPackage>>();
    public ZRpc Remote;
    public readonly Queue<Tuple<string, byte[]>> Inbox = new Queue<Tuple<string, byte[]>>();
    public byte[] LastSent;
    public int Sends, MaxPacket;
    public void Register<T>(string name, Action<ZRpc, T> handler) => handlers[name] = (rpc, pkg) => handler(rpc, (T)(object)pkg);
    public void Unregister(string name) => handlers.Remove(name);
    public void Invoke(string name, params object[] args)
    {
        var bytes = ((ZPackage)args[0]).GetArray(); LastSent = bytes; Sends++; MaxPacket = Math.Max(MaxPacket, bytes.Length + 8);
        Remote?.Inbox.Enqueue(Tuple.Create(name, bytes));
    }
    public void Drain()
    {
        while (Inbox.Count > 0) { var next = Inbox.Dequeue(); if (handlers.TryGetValue(next.Item1, out var handler)) handler(this, new ZPackage(next.Item2)); }
    }
    public void Inject(byte[] bytes) { foreach (var h in handlers.Values) h(this, new ZPackage(bytes)); }
    public int Registrations => handlers.Count;
}
public sealed class ZSteamSocket
{
    public string Account = "76561198000000001";
    public string GetHostName() => Account;
    private readonly Queue<byte[]> m_sendQueue = new Queue<byte[]>();
    public bool IsConnected() => true;
    public void Congest(bool value) { m_sendQueue.Clear(); if (value) m_sendQueue.Enqueue(new byte[1]); }
}
public sealed class ZNetPeer
{
    public long m_uid;
    public string m_playerName = "Test player";
    public ZRpc m_rpc = new ZRpc();
    public ZSteamSocket m_socket = new ZSteamSocket();
    public bool Ready = true;
    public bool IsReady() => Ready;
}
public sealed class ZNet
{
    public static ZNet instance;
    public bool Server;
    public bool Dedicated, Saving, ThrowPeers;
    public readonly List<ZNetPeer> Peers = new List<ZNetPeer>();
    public bool IsServer() => Server;
    public bool IsDedicated() => Dedicated;
    public bool IsSaving() => Saving;
    public List<ZNetPeer> GetPeers() => ThrowPeers ? throw new InvalidOperationException("peer inspection failed") : Peers;
    public static implicit operator bool(ZNet n) => n != null;
}
public sealed class Player
{
    public static Player m_localPlayer = new Player();
    public static implicit operator bool(Player p) => p != null;
}
namespace UnityEngine
{
    public enum KeyCode { F9 }
    public static class Application { public static bool isFocused = true; public static int targetFrameRate = -1; }
    public static class QualitySettings { public static int vSyncCount; }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigDescription { public ConfigDescription(string text, object range) { } }
    public sealed class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile
    {
        public bool Share = true, Receive = true, Names = true;
        public bool IdleEnabled = true;
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) => new ConfigEntry<T> {
            Value = typeof(T) == typeof(bool) ? (T)(object)(section == "IdleServer" ? IdleEnabled : key == "ShareWithServer" ? Share : key == "ReceiveFromClients" ? Receive : Names) : value };
        public ConfigEntry<T> Bind<T>(string section, string key, T value, ConfigDescription description) => new ConfigEntry<T> { Value = value };
    }
    public struct KeyboardShortcut
    {
        public static bool Down;
        public KeyboardShortcut(UnityEngine.KeyCode key) { }
        public bool IsDown() => Down;
    }
}
namespace ValheimBoosted
{
    internal sealed class TelemetryIntegration
    {
        internal bool GameSupported = true;
        internal readonly Dictionary<string, FeatureStatus> Features = new Dictionary<string, FeatureStatus>();
    }
    internal static class TelemetryCollector { internal static double Now; }
}

internal static class ClientTransportChecks
{
    private sealed class Pair : IDisposable
    {
        internal readonly ZNet Server = new ZNet { Server = true }, Client = new ZNet();
        internal readonly ZNetPeer ServerPeer = new ZNetPeer { m_uid = 42 }, ClientPeer = new ZNetPeer { m_uid = 99 };
        internal readonly ClientTelemetryTransport Receiver, Sender;
        internal Pair(bool sharing = true, bool receiving = true, bool supported = true)
        {
            TelemetryCollector.Now = 100;
            Server.Peers.Add(ServerPeer); Client.Peers.Add(ClientPeer);
            ServerPeer.m_rpc.Remote = ClientPeer.m_rpc; ClientPeer.m_rpc.Remote = ServerPeer.m_rpc;
            Receiver = new ClientTelemetryTransport(new BepInEx.Configuration.ConfigFile { Receive = receiving }, new TelemetryIntegration { GameSupported = supported });
            Sender = new ClientTelemetryTransport(new BepInEx.Configuration.ConfigFile { Share = sharing }, new TelemetryIntegration { GameSupported = supported });
        }
        internal void TickServer(double at) { TelemetryCollector.Now = at; ZNet.instance = Server; Receiver.Tick(at); }
        internal void TickClient(double at) { TelemetryCollector.Now = at; ZNet.instance = Client; Sender.Tick(at); }
        internal void Handshake()
        {
            TickServer(100); TickClient(100);
            TelemetryCollector.Now = 100.005; ServerPeer.m_rpc.Drain();
            TelemetryCollector.Now = 100.01; ClientPeer.m_rpc.Drain();
            TickServer(101.1);
            TelemetryCollector.Now = 101.11; ClientPeer.m_rpc.Drain();
            TelemetryCollector.Now = 101.12; ServerPeer.m_rpc.Drain();
        }
        internal TelemetrySnapshot Snapshot(double time, bool server = false) => new TelemetrySnapshot {
            role = server ? "dedicated_server" : "client", running = true, sampleWindowSeconds = 1, windowEndMonotonicMs = time * 1000,
            frameIntervalMs = new TimingSummary { samples = 60, mean = 16, p95 = 18, p99 = 22, max = 25 },
            managedMemoryBytes = 128 * 1048576, gcCollections = new[] { 1, 0, 0 }, longFrames50Ms = 0, longFrames100Ms = 0, longFrames250Ms = 0,
            worstFrameEndMonotonicMs = time * 1000 - 100,
            peers = new[] { new PeerMetrics { peerSessionId = server ? "42" : "99", measurementStatus = "available", rttMs = 40, applicationQueuedBytes = 0, estimatedTransportQueueMs = 0 } } };
        internal ClientTelemetrySnapshot Export(double at) { var s = Snapshot(at, true); Receiver.Capture(s); return s.clientTelemetry; }
        public void Dispose() { Sender.Dispose(); Receiver.Dispose(); ZNet.instance = null; }
    }

    internal static void Run(Action<bool, string> check)
    {
        var identityKey = PlayerIdentity.NewKey();
        var identity = new PlayerIdentity(identityKey);
        var playerId = identity.Steam("76561198000000001");
        check(identityKey.Length == 64 && playerId.Length == 64 && !playerId.Contains("76561198000000001"), "Identity uses a pseudonym, never the raw Steam account");
        check(new PlayerIdentity(identityKey).Steam("76561198000000001") == playerId, "Preserved server key recognizes the account after restart");
        check(new PlayerIdentity(PlayerIdentity.NewKey()).Steam("76561198000000001") != playerId && identity.Steam("76561198000000002") != playerId, "Different servers and accounts receive different identities");
        check(!new PlayerIdentity("bad").Available && !new PlayerIdentity(new string('z', 64)).Available && identity.Steam("0") == null && identity.Steam("127.0.0.1") == null, "Invalid keys and non-account host names never become stable identities");
        var clock = new ClientClock();
        check(clock.Update(1000, 515, 520, 1045), "Clock accepts a four-timestamp exchange");
        check(clock.Offset == 505 && clock.Error == 20, "Clock accounts for remote processing time");
        check(clock.Uncertainty(2045) > 20 && !clock.Current(200000), "Clock drift allowance and expiration are explicit");
        check(!clock.Update(1000, double.NaN, 520, 1045) && !clock.Update(1000, 600, 500, 1045), "Malformed clock samples rejected");

        var sample = new ClientWindow { sequence = 1, startMs = 1000, endMs = 2000, frames = 60, frameMeanMs = 16,
            frameP95Ms = 18, frameP99Ms = 22, frameMaxMs = 25, worstFrameEndMs = 1900, focused = true, frameLimit = -1, markerMs = 1950 };
        byte[] data;
        using (var stream = new MemoryStream()) { using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) ClientTelemetryCodec.Write(w, sample); data = stream.ToArray(); }
        using (var r = new BinaryReader(new MemoryStream(data))) {
            var copy = ClientTelemetryCodec.Read(r);
            check(copy.frameMeanMs == 16 && copy.markerMs == 1950 && copy.rttMs == null, "Binary windows preserve units, markers and unavailable metrics");
        }
        check(5 * data.Length + 13 <= 1024, "Five-window batch including headers stays within 1 KiB");
        bool truncated = false;
        try { using var r = new BinaryReader(new MemoryStream(data, 0, data.Length - 1)); ClientTelemetryCodec.Read(r); } catch (EndOfStreamException) { truncated = true; }
        check(truncated, "Truncated windows rejected");

        using (var pair = new Pair())
        {
            pair.Handshake(); check(pair.Sender.Status == "not_reporting" || pair.Sender.Status == "sharing", "Handshake does not affect the game connection");
            pair.Sender.Capture(pair.Snapshot(101.2)); pair.TickClient(102.2);
            TelemetryCollector.Now = 102.21; pair.ServerPeer.m_rpc.Drain();
            var export = pair.Export(102.21);
            check(export.samples.Length == 1 && export.samples[0].sample.frames == 60, "Client measurements reach the server through the production protocol");
            check(export.samples[0].peerSessionId == "42" && export.peers[0].name == "Test player", "Server assigns identity from the actual RPC peer");
            check(export.samples[0].receivedAtMs - export.samples[0].endMs > 900 && export.samples[0].clockErrorMs < 20, "Delayed arrival does not shift sample occurrence time");
            byte[] replay = pair.ClientPeer.m_rpc.LastSent;
            pair.ServerPeer.m_rpc.Inject(replay); pair.ServerPeer.m_rpc.Inject(new byte[1025]);
            check(pair.Export(102.22).rejectedMessages >= 2 && pair.Export(102.22).samples.Length == 1, "Replay and oversize messages are rejected without corrupting history");
            pair.ClientPeer.m_socket.Congest(true); pair.Sender.Capture(pair.Snapshot(103.2)); pair.TickClient(104.3);
            check(pair.Export(104.3).samples.Length == 1, "Congested connections do not enqueue extra telemetry");
            pair.ClientPeer.m_socket.Congest(false); pair.TickClient(106.4); TelemetryCollector.Now = 106.41; pair.ServerPeer.m_rpc.Drain();
            check(pair.Export(106.41).samples.Length == 2, "Bounded backlog resumes when congestion clears");
            check(pair.ClientPeer.m_rpc.MaxPacket <= 1024, "All transmitted RPC packets respect the byte cap");
            BepInEx.Configuration.KeyboardShortcut.Down = true; pair.TickClient(107.5); BepInEx.Configuration.KeyboardShortcut.Down = false;
            pair.Sender.Capture(pair.Snapshot(107.6)); pair.TickClient(108.6); TelemetryCollector.Now = 108.61; pair.ServerPeer.m_rpc.Drain();
            check(pair.Export(108.61).samples[2].sample.markerMs == 107500, "Lag marker records the client's input processing time");
            pair.TickClient(109.7); // Warm the fast path, then measure only unchanged per-frame calls.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) pair.Sender.Tick(109.71);
            check(GC.GetAllocatedBytesForCurrentThread() == before, "Telemetry transport's per-frame fast path allocates no managed memory");
            pair.ServerPeer.Ready = false; pair.TickServer(111);
            check(pair.ServerPeer.m_rpc.Registrations == 0, "Disconnect removes telemetry handlers");
            var departed = pair.Export(111).sessions[0];
            check(departed.sessionEndedAtMs.HasValue && departed.sessionEndedAtMs >= departed.lastSeenAtMs, "Disconnect exports a closed session with last-observed time");
            pair.ServerPeer.Ready = true; pair.ServerPeer.m_uid = 84; pair.ServerPeer.m_playerName = "Renamed character"; pair.TickServer(113);
            var relog = pair.Export(113);
            check(relog.peers[0].playerId == departed.playerId && relog.peers[0].streamId != departed.streamId, "Relog with changed peer ID and character creates a new session for the same account");
            check(relog.sessions.Length == 2 && relog.sessions[1].name == "Test player" && relog.sessions[1].peerSessionId == "42", "Ended sessions retain their original character and peer identity");
            pair.ServerPeer.Ready = false; pair.TickServer(115);
            pair.ServerPeer.Ready = true; pair.ServerPeer.m_socket.Account = "76561198000000002"; pair.TickServer(117);
            check(pair.Export(117).peers[0].playerId != departed.playerId, "Identical character names do not merge different Steam accounts");
            pair.TickServer(180);
            check(pair.Export(180).sessions.Length == 1, "Disconnected session export buffer expires after sixty seconds");
        }
        using (var pair = new Pair())
        {
            pair.Handshake(); pair.Sender.Capture(pair.Snapshot(101.2)); pair.Sender.Capture(pair.Snapshot(102.2)); pair.TickClient(103.2);
            byte[] packet = pair.ClientPeer.m_rpc.LastSent;
            byte[] broken = new byte[packet.Length - 1]; Array.Copy(packet, broken, broken.Length);
            TelemetryCollector.Now = 103.21; pair.ServerPeer.m_rpc.Inject(broken);
            check(pair.Export(103.21).samples.Length == 0, "Malformed final record rejects the entire batch without accepting its first record");
            pair.ServerPeer.m_rpc.Drain();
            check(pair.Export(103.21).samples.Length == 2, "A malformed batch does not consume sequence numbers from the valid retry");
            for (int i = 0; i < 100; i++) pair.ServerPeer.m_rpc.Inject(packet);
            check(pair.Export(103.21).rejectedMessages >= 101 && pair.Export(103.21).samples.Length == 2, "Flood and replay rejection keep the retained buffer unchanged");
        }
        using (var pair = new Pair())
        {
            pair.Handshake(); pair.ClientPeer.m_socket.Congest(true);
            for (int i = 0; i < 75; i++) pair.Sender.Capture(pair.Snapshot(102 + i));
            pair.TickClient(177);
            var local = pair.Snapshot(177); local.running = false; pair.Sender.Capture(local);
            check(local.clientTelemetry.droppedSamples >= 15 && local.clientTelemetry.congestionSkips > 0, "Congestion cannot grow the sample backlog beyond its fixed capacity");
            pair.ClientPeer.m_socket.Congest(false); pair.TickClient(180); TelemetryCollector.Now = 180.01; pair.ServerPeer.m_rpc.Drain();
            check(pair.Export(180.01).samples.Length == 5 && pair.ClientPeer.m_rpc.MaxPacket <= 1024, "Backlog recovery remains capped to five windows per packet");
        }
        using (var pair = new Pair(sharing: false)) { pair.Handshake(); pair.Sender.Capture(pair.Snapshot(101.2)); pair.TickClient(102.2); check(pair.Export(102.2).samples.Length == 0 && pair.Export(102.2).peers[0].status == "sharing_disabled", "Client sharing switch is honored"); }
        using (var pair = new Pair())
        {
            pair.Handshake(); pair.ClientPeer.m_socket.Congest(true); pair.Sender.Capture(pair.Snapshot(101.2));
            pair.ClientPeer.Ready = false; pair.TickClient(103); pair.ClientPeer.Ready = true; pair.ClientPeer.m_socket.Congest(false);
            pair.TickClient(105); TelemetryCollector.Now = 105.01; pair.ServerPeer.m_rpc.Drain();
            TelemetryCollector.Now = 105.02; pair.ClientPeer.m_rpc.Drain(); pair.TickClient(107);
            TelemetryCollector.Now = 107.01; pair.ServerPeer.m_rpc.Drain();
            check(pair.Export(107.01).samples.Length == 0, "Client reconnect discards old-session backlog before a new handshake can send it");
        }
        using (var pair = new Pair())
        {
            for (int batch = 0; batch < 4; batch++)
            {
                pair.Server.Peers.Clear();
                for (int i = 0; i < 64; i++) pair.Server.Peers.Add(new ZNetPeer { m_uid = 1000 + batch * 64 + i });
                pair.TickServer(100 + batch * 2);
            }
            var bounded = pair.Export(106);
            check(bounded.peers.Length == 64 && bounded.sessions.Length == 192, "Connection churn bounds exported session metadata to 64 active and 128 ended records");
        }
        using (var pair = new Pair(receiving: false)) { pair.Handshake(); check(pair.Export(102.2).peers[0].status == "receiving_disabled", "Server reception switch is honored"); }
        using (var pair = new Pair(supported: false)) { pair.Handshake(); check(pair.ClientPeer.m_rpc.Sends == 0 && pair.ServerPeer.m_rpc.Registrations == 0, "Unsupported game versions install no telemetry RPCs"); }
    }
}
