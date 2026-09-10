using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimBoosted;

// Public per-peer RPC registration avoids adding Harmony hooks to the game loop.
internal sealed class ClientTelemetryTransport : IDisposable
{
    private const string RpcName = "valheim.boosted.telemetry.v1";
    private const ushort Magic = 0xb007;
    private enum Kind : byte { Hello = 1, Welcome = 2, Sync = 3, SyncReply = 4, Samples = 5 }
    private sealed class PeerState
    {
        internal ZNetPeer Peer;
        internal string Stream = Guid.NewGuid().ToString("N"), Build, PlayerId, PeerId, Name, Status = "not_reporting";
        internal double Started, LastSeen;
        internal double? Ended;
        internal bool Active, Congested;
        internal double NextHello, NextSync, SyncSent, LastReceived, BudgetAt;
        internal int HelloAttempts, Challenge;
        internal double Tokens = 4096;
        internal long LastSequence;
        internal readonly ClientClock Clock = new ClientClock();
    }
    private readonly Dictionary<ZRpc, PeerState> peers = new Dictionary<ZRpc, PeerState>();
    private readonly Queue<ClientWindow> pending = new Queue<ClientWindow>(60);
    private readonly Queue<ClientTelemetryPoint> recent = new Queue<ClientTelemetryPoint>(512);
    private readonly Queue<PeerState> retired = new Queue<PeerState>(128);
    private readonly PlayerIdentity identities;
    private readonly bool share, receive, includeNames;
    private readonly KeyboardShortcut markerKey;
    private readonly FeatureStatus feature;
    private readonly double originMono = TelemetryCollector.Now * 1000;
    private readonly double originUtc = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
    private readonly FieldInfo queueField;
    private ZNet net;
    private bool server;
    private double nextTick, nextSend, marker, lastMarker;
    private long sequence, sentBytes, receivedBytes, rejected, congested, dropped;
    internal string Status { get; private set; } = "waiting_for_connection";
    internal double UtcMs(double monotonicMs) => originUtc + monotonicMs - originMono;

    internal ClientTelemetryTransport(ConfigFile config, TelemetryIntegration integration)
    {
        share = config.Bind("ClientTelemetry", "ShareWithServer", true, "Share bounded performance summaries with a compatible server. Restart required. No Steam IDs or hardware identifiers are sent.").Value;
        receive = config.Bind("ClientTelemetry", "ReceiveFromClients", true, "Accept client performance summaries on servers. Restart required.").Value;
        includeNames = config.Bind("ClientTelemetry", "IncludePlayerNames", true, "Include server-known character names in admin history. Disable to use server-scoped player IDs, or session IDs when identity is unavailable. Restart required.").Value;
        markerKey = config.Bind("ClientTelemetry", "MarkLagKey", new KeyboardShortcut(KeyCode.F9), "Mark a lag incident at the time this client processes the key. At most one marker every five seconds.").Value;
        identities = new PlayerIdentity(config.Bind("ClientTelemetry", "IdentityKey", PlayerIdentity.NewKey(),
            "Private server identity key, generated once. Preserve this config across restarts to recognize returning Steam accounts. Never share this key; changing it starts a new player identity namespace. Restart required.").Value);
        feature = new FeatureStatus { id = "ClientTelemetry", enabled = share || receive,
            status = !share && !receive ? "configured_disabled" : integration.GameSupported ? "available" : "blocked_compatibility" };
        integration.Features[feature.id] = feature;
        queueField = typeof(ZSteamSocket).GetField("m_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic);
        if (feature.Collect && (queueField == null || queueField.FieldType != typeof(Queue<byte[]>)))
        { feature.status = "contract_mismatch"; feature.detail = "Steam managed queue contract changed; telemetry RPCs disabled"; }
    }

    internal void Tick(double now)
    {
        if (!feature.Collect) { Status = feature.status; return; }
        if (share && net && !server && markerKey.IsDown() && now - lastMarker >= 5)
        { marker = now * 1000; lastMarker = now; }
        if (now < nextTick) return;
        nextTick = now + 1;
        TickSlow(now);
    }

    // Keep lambdas and their compiler-generated closures out of the per-frame fast path.
    private void TickSlow(double now)
    {
        var current = ZNet.instance;
        if (!ReferenceEquals(net, current)) { Clear(); net = current; server = net && net.IsServer(); }
        if (!net) { Status = "waiting_for_connection"; return; }
        var live = net.GetPeers();
        foreach (var rpc in peers.Where(p => !live.Contains(p.Value.Peer) || !p.Value.Peer.IsReady() || !p.Value.Peer.m_socket.IsConnected()).Select(p => p.Key).ToArray())
        {
            var state = peers[rpc]; state.Ended = UtcMs(now * 1000); state.Status = "disconnected";
            if (server) retired.Enqueue(state);
            else { pending.Clear(); marker = 0; sequence = 0; nextSend = 0; }
            rpc.Unregister(RpcName); peers.Remove(rpc);
        }
        foreach (var peer in live)
        {
            if (!peer.IsReady() || !peer.m_socket.IsConnected() || peers.ContainsKey(peer.m_rpc) || peers.Count >= 64) continue;
            var state = new PeerState { Peer = peer, NextHello = now + (peers.Count % 3) * 0.2,
                Started = UtcMs(now * 1000), LastSeen = UtcMs(now * 1000), PeerId = Id(peer), Name = includeNames ? Limit(peer.m_playerName, 64) : null,
                PlayerId = server && peer.m_socket is ZSteamSocket steam ? identities.Steam(steam.GetHostName()) : null };
            peers.Add(peer.m_rpc, state);
            peer.m_rpc.Register<ZPackage>(RpcName, OnMessage);
        }
        foreach (var state in peers.Values)
        {
            state.LastSeen = UtcMs(now * 1000);
            state.Name = includeNames ? Limit(state.Peer.m_playerName, 64) : null;
            if (!server && state.HelloAttempts < 3 && !state.Active && now >= state.NextHello)
            {
                if (Send(state, Kind.Hello, w => { w.Write(share); w.Write(typeof(Plugin).Module.ModuleVersionId.ToByteArray()); }))
                { state.HelloAttempts++; state.NextHello = now + 10; }
            }
            if (server && state.Active && now >= state.NextSync)
            {
                int challenge = state.Challenge + 1;
                double s1 = UtcMs(TelemetryCollector.Now * 1000);
                if (Send(state, Kind.Sync, w => w.Write(challenge)))
                { state.Challenge = challenge; state.SyncSent = s1; state.NextSync = now + 30; }
            }
        }
        if (server) Status = receive ? "receiving" : "receiving_disabled";
        else if (!share) Status = "sharing_disabled";
        else
        {
            var peer = peers.Values.FirstOrDefault();
            Status = peer?.Status ?? "waiting_for_connection";
            if (peer != null && peer.Active && now >= nextSend && pending.Count > 0)
            {
                nextSend = now + 2;
                while (pending.Count > 0 && now * 1000 - pending.Peek().endMs > 60000) { pending.Dequeue(); dropped++; }
                if (pending.Count > 0)
                {
                    int count = Math.Min(ClientTelemetryCodec.MaxBatch, pending.Count);
                    if (Send(peer, Kind.Samples, w => { w.Write((byte)count); int n = 0; foreach (var sample in pending) { if (n++ == count) break; ClientTelemetryCodec.Write(w, sample); } }))
                        for (int i = 0; i < count; i++) pending.Dequeue();
                }
            }
        }
        Prune(UtcMs(now * 1000));
    }

    private bool Send(PeerState state, Kind kind, Action<BinaryWriter> write)
    {
        // Do not add telemetry to an already backed-up game queue. Native metrics are cached by Capture.
        if (state.Congested || state.Peer.m_socket is ZSteamSocket steam && ((Queue<byte[]>)queueField.GetValue(steam)).Count > 0)
        { congested++; return false; }
        if (!state.Peer.m_socket.IsConnected()) return false;
        using (var stream = new MemoryStream(1024))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic); writer.Write((byte)ClientTelemetryCodec.Version); writer.Write((byte)kind); write(writer);
            if (stream.Length > ClientTelemetryCodec.MaxPacketBytes - 8) { dropped++; return false; }
            byte[] data = stream.ToArray();
            state.Peer.m_rpc.Invoke(RpcName, new ZPackage(data));
            sentBytes += data.Length + 8;
            return true;
        }
    }

    private void OnMessage(ZRpc rpc, ZPackage package)
    {
        if (!feature.Collect || !peers.TryGetValue(rpc, out var state) || !state.Peer.IsReady()) return;
        double now = TelemetryCollector.Now, received = UtcMs(now * 1000);
        int size = package.Size();
        state.Tokens = Math.Min(4096, state.Tokens + Math.Max(0, now - state.BudgetAt) * 1024); state.BudgetAt = now;
        // Charge a minimum cost for tiny-message floods as well as an explicit byte budget.
        int cost = Math.Max(256, size);
        if (size < 4 || size > ClientTelemetryCodec.MaxPacketBytes || state.Tokens < cost) { rejected++; return; }
        state.Tokens -= cost; receivedBytes += size + 8;
        try
        {
            using (var stream = new MemoryStream(package.GetArray(), false))
            using (var r = new BinaryReader(stream))
            {
                if (r.ReadUInt16() != Magic || r.ReadByte() != ClientTelemetryCodec.Version) throw new InvalidDataException("Unsupported protocol");
                var kind = (Kind)r.ReadByte();
                if (server && kind == Kind.Hello)
                {
                    bool sharing = r.ReadBoolean(); byte[] build = r.ReadBytes(16); End(stream);
                    if (build.Length != 16) throw new InvalidDataException("Invalid build ID");
                    state.Build = new Guid(build).ToString(); state.Active = receive && sharing;
                    state.Status = !receive ? "receiving_disabled" : sharing ? "synchronizing" : "sharing_disabled";
                    Send(state, Kind.Welcome, w => w.Write(state.Active));
                }
                else if (!server && kind == Kind.Welcome)
                {
                    bool accepted = r.ReadBoolean(); End(stream);
                    state.Active = share && accepted; state.HelloAttempts = 3;
                    state.Status = state.Active ? "sharing" : "server_declined";
                }
                else if (!server && state.Active && kind == Kind.Sync)
                {
                    int challenge = r.ReadInt32(); End(stream);
                    double c2 = now * 1000;
                    Send(state, Kind.SyncReply, w => { w.Write(challenge); w.Write(c2); w.Write(TelemetryCollector.Now * 1000); });
                }
                else if (server && state.Active && kind == Kind.SyncReply)
                {
                    int challenge = r.ReadInt32(); double c2 = r.ReadDouble(), c3 = r.ReadDouble(); End(stream);
                    if (challenge != state.Challenge || state.SyncSent == 0 || !state.Clock.Update(state.SyncSent, c2, c3, received)) throw new InvalidDataException("Invalid clock exchange");
                    state.SyncSent = 0; state.Status = "reporting";
                }
                else if (server && state.Active && kind == Kind.Samples)
                {
                    int count = r.ReadByte();
                    if (count < 1 || count > ClientTelemetryCodec.MaxBatch) throw new InvalidDataException("Invalid batch count");
                    var samples = new ClientWindow[count];
                    long last = state.LastSequence;
                    for (int i = 0; i < count; i++)
                    {
                        var sample = samples[i] = ClientTelemetryCodec.Read(r);
                        if (sample.sequence <= last) throw new InvalidDataException("Duplicate or reordered sample");
                        last = sample.sequence;
                        double end = sample.endMs + state.Clock.Offset;
                        if (state.Clock.Current(received) && (end > received + state.Clock.Uncertainty(received) + 250 || end < received - 180000)) throw new InvalidDataException("Sample time outside allowed range");
                    }
                    End(stream);
                    state.LastSequence = last; state.LastReceived = received;
                    if (!state.Clock.Current(received)) { state.Status = "clock_unavailable"; dropped += count; state.NextSync = now; return; }
                    foreach (var sample in samples)
                        recent.Enqueue(new ClientTelemetryPoint { streamId = state.Stream, peerSessionId = state.PeerId, playerId = state.PlayerId,
                            startMs = sample.startMs + state.Clock.Offset, endMs = sample.endMs + state.Clock.Offset,
                            receivedAtMs = received, clockErrorMs = state.Clock.Uncertainty(received), sample = sample });
                    state.Status = "reporting"; Prune(received);
                }
                else throw new InvalidDataException("Unexpected telemetry message");
                feature.invocations++; feature.status = "active";
            }
        }
        catch (Exception) { rejected++; } // Malformed optional telemetry must never disconnect a game peer.
    }

    private static void End(MemoryStream stream) { if (stream.Position != stream.Length) throw new InvalidDataException("Trailing data"); }
    private static string Id(ZNetPeer peer) => peer.m_uid.ToString(CultureInfo.InvariantCulture);
    private void Prune(double now)
    {
        while (recent.Count > 512 || recent.Count > 0 && now - recent.Peek().receivedAtMs > 60000) recent.Dequeue();
        while (retired.Count > 128 || retired.Count > 0 && now - retired.Peek().Ended > 60000) retired.Dequeue();
    }

    internal void Capture(TelemetrySnapshot snapshot)
    {
        double now = UtcMs(snapshot.windowEndMonotonicMs);
        snapshot.clockUtcMs = now;
        foreach (var state in peers.Values)
        {
            var metric = snapshot.peers?.FirstOrDefault(p => p.peerSessionId == Id(state.Peer));
            state.Congested = metric != null && (metric.applicationQueuedBytes > 0 || metric.pendingReliableBytes > 65536 || metric.estimatedTransportQueueMs > 250);
        }
        if (!server && share && peers.Values.Any(p => p.Active) && snapshot.running && snapshot.role == "client")
        {
            double end = snapshot.windowEndMonotonicMs, start = end - snapshot.sampleWindowSeconds * 1000;
            var p = snapshot.peers?.FirstOrDefault();
            if (snapshot.sampleWindowSeconds <= 120)
            {
                while (pending.Count >= 60) { pending.Dequeue(); dropped++; }
                pending.Enqueue(new ClientWindow { sequence = ++sequence, startMs = start, endMs = end,
                    frames = snapshot.frameIntervalMs?.samples ?? 0, frameMeanMs = snapshot.frameIntervalMs?.mean,
                    frameP95Ms = snapshot.frameIntervalMs?.p95, frameP99Ms = snapshot.frameIntervalMs?.p99, frameMaxMs = snapshot.frameIntervalMs?.max,
                    worstFrameEndMs = snapshot.worstFrameEndMonotonicMs, longFrames50 = snapshot.longFrames50Ms, longFrames100 = snapshot.longFrames100Ms, longFrames250 = snapshot.longFrames250Ms,
                    gc0 = snapshot.gcCollections?[0] ?? 0, gc1 = snapshot.gcCollections?[1] ?? 0, gc2 = snapshot.gcCollections?[2] ?? 0,
                    cpuPercent = snapshot.resources?.cpuPercentOneCore, managedMemoryMiB = snapshot.managedMemoryBytes / 1048576.0,
                    networkP95Ms = snapshot.networkUpdateDurationMs?.p95, rttMs = p?.rttMs, queueMs = p?.estimatedTransportQueueMs,
                    changedZdos = snapshot.clientChangedZdos, focused = Application.isFocused, loading = !Player.m_localPlayer,
                    frameLimit = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
                    markerMs = marker >= start && marker <= end ? (double?)marker : null });
            }
            else dropped++;
            marker = 0;
        }
        Prune(now);
        snapshot.clientTelemetry = new ClientTelemetrySnapshot { receiveEnabled = receive, shareEnabled = share, status = Status,
            identityStatus = identities.Available ? "server_scoped_steam" : "identity_key_invalid",
            sentBytes = sentBytes, receivedBytes = receivedBytes, rejectedMessages = rejected, congestionSkips = congested, droppedSamples = dropped,
            samples = server ? recent.ToArray() : new ClientTelemetryPoint[0],
            peers = server ? peers.Values.Select(p => Describe(p, now)).ToArray() : new ClientTelemetryPeer[0],
            sessions = server ? peers.Values.Concat(retired).Select(p => Describe(p, now)).ToArray() : new ClientTelemetryPeer[0] };
    }
    private ClientTelemetryPeer Describe(PeerState p, double now) => new ClientTelemetryPeer { streamId = p.Stream, peerSessionId = p.PeerId, playerId = p.PlayerId,
        sessionStartedAtMs = p.Started, sessionEndedAtMs = p.Ended, lastSeenAtMs = p.LastSeen,
        name = p.Name, modBuildId = p.Build, status = p.Status,
        clockErrorMs = p.Clock.Current(now) ? (double?)p.Clock.Uncertainty(now) : null,
        lastReceivedAtMs = p.LastReceived > 0 ? (double?)p.LastReceived : null };
    private static string Limit(string value, int length) => value == null ? null : value.Substring(0, Math.Min(value.Length, length));
    internal string MarkerShortcut => markerKey.ToString();
    private void Clear() { foreach (var rpc in peers.Keys) rpc.Unregister(RpcName); peers.Clear(); pending.Clear(); recent.Clear(); retired.Clear(); sequence = 0; marker = 0; }
    public void Dispose() => Clear();
}
