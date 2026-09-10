using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class TelemetryCollector
{
    private sealed class PeerState
    {
        public double? previousRtt;
        public double? lastBatchAt;
        public int batches;
    }

    private static readonly FieldInfo Instances = AccessTools.Field(typeof(ZNetScene), "m_instances");
    private readonly Dictionary<ZRpc, PeerState> peerStates = new Dictionary<ZRpc, PeerState>();
    private readonly SampleWindow frames = new SampleWindow();
    private readonly SampleWindow network = new SampleWindow();
    private readonly string processSession = Guid.NewGuid().ToString("N");
    private readonly double startedAt = Now;
    private readonly int[] previousGc = new int[3];
    private ZNet previousNet;
    private double lastFrameAt;
    private double lastSampleAt;
    private long sequence;
    private int fixedUpdates;
    private readonly TelemetryIntegration integration;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public TelemetryCollector(TelemetryIntegration integration)
    {
        this.integration = integration;
        lastFrameAt = lastSampleAt = Now;
        for (int i = 0; i < previousGc.Length; i++) previousGc[i] = GC.CollectionCount(i);
    }

    public void Frame()
    {
        double now = Now;
        if (integration.Features["FrameTiming"].Collect)
            frames.Add((now - lastFrameAt) * 1000);
        lastFrameAt = now;
    }

    public void FixedStep() => fixedUpdates++;
    public void NetworkUpdate(double milliseconds) => network.Add(milliseconds);

    public void ReceivedBatch(ZRpc rpc)
    {
        if (!peerStates.TryGetValue(rpc, out var state)) return;
        state.lastBatchAt = Now;
        state.batches++;
    }

    public TelemetrySnapshot Capture()
    {
        double now = Now;
        var net = ZNet.instance;
        if (!ReferenceEquals(previousNet, net))
        {
            peerStates.Clear();
            previousNet = net;
        }
        bool isServer = net && net.IsServer();
        var snapshot = new TelemetrySnapshot
        {
            processSession = processSession,
            worldSession = net ? ZDOMan.GetSessionID().ToString(CultureInfo.InvariantCulture) : null,
            sequence = ++sequence,
            capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            uptimeSeconds = now - startedAt,
            sampleWindowSeconds = now - lastSampleAt,
            role = !net ? "menu" : isServer ? (net.IsDedicated() ? "dedicated_server" : "host") : "client",
            frameIntervalMs = frames.Take(),
            networkUpdateDurationMs = network.Take(),
            networkTimingStatus = integration.Features["NetworkTiming"].status,
            zdoReceiveStatus = integration.Features["ZdoReceive"].status,
            fixedUpdates = fixedUpdates,
            fixedStepSeconds = UnityEngine.Time.fixedDeltaTime,
            managedMemoryBytes = GC.GetTotalMemory(false),
            gcCollections = new int[3],
        };
        for (int i = 0; i < previousGc.Length; i++)
        {
            int current = GC.CollectionCount(i);
            snapshot.gcCollections[i] = current - previousGc[i];
            previousGc[i] = current;
        }
        fixedUpdates = 0;
        lastSampleAt = now;
        var peers = new List<PeerMetrics>();
        var live = new HashSet<ZRpc>();
        if (net && integration.GameSupported)
        {
            foreach (var peer in net.GetPeers())
            {
                if (!peer.IsReady()) continue;
                live.Add(peer.m_rpc);
                if (!peerStates.TryGetValue(peer.m_rpc, out var state))
                    peerStates[peer.m_rpc] = state = new PeerState();
                var metrics = new PeerMetrics
                {
                    peerSessionId = peer.m_uid.ToString(CultureInfo.InvariantCulture),
                    transport = peer.m_socket.GetType().Name,
                    connected = peer.m_socket.IsConnected(),
                    lastZdoBatchReceivedAgoSeconds = state.lastBatchAt.HasValue ? (double?)(now - state.lastBatchAt.Value) : null,
                    zdoBatchesReceivedInWindow = state.batches,
                    measurementStatus = "unsupported_transport",
                };
                state.batches = 0;
                try
                {
                    if (!integration.Features["SteamTransport"].Collect) metrics.measurementStatus = integration.Features["SteamTransport"].status;
                    else if (metrics.connected && peer.m_socket is ZSteamSocket steam) SteamMetrics.Read(steam, metrics);
                    else if (!metrics.connected) metrics.measurementStatus = "disconnected";
                }
                catch (Exception ex) { metrics.measurementStatus = "unavailable:" + ex.GetType().Name; }
                if (metrics.rttMs.HasValue && state.previousRtt.HasValue)
                    metrics.rttSampleDeltaMs = Math.Abs(metrics.rttMs.Value - state.previousRtt.Value);
                state.previousRtt = metrics.rttMs;
                peers.Add(metrics);
            }
        }
        foreach (var rpc in new List<ZRpc>(peerStates.Keys))
            if (!live.Contains(rpc)) peerStates.Remove(rpc);
        snapshot.peers = peers.ToArray();
        snapshot.readyPeers = peers.Count;
        var zdos = ZDOMan.instance;
        if (net && zdos != null && integration.Features["GameCounters"].Collect)
        {
            snapshot.knownZdos = zdos.NrOfObjects();
            snapshot.zdosSentLastGameSecond = zdos.GetSentZDOs();
            snapshot.zdosReceivedLastGameSecond = zdos.GetRecvZDOs();
            if (!isServer) snapshot.clientChangedZdos = zdos.GetClientChangeQueue();
        }
        if (integration.Features["Ownership"].Collect) ReadOwnership(snapshot);
        else snapshot.ownershipStatus = integration.Features["Ownership"].status;
        snapshot.compatibility = integration.Compatibility.Copy();
        snapshot.features = integration.Snapshot();
        return snapshot;
    }

    private static void ReadOwnership(TelemetrySnapshot snapshot)
    {
        snapshot.ownershipStatus = "scene_unavailable";
        var scene = ZNetScene.instance;
        if (!scene) return;
        try
        {
            if (!(Instances?.GetValue(scene) is Dictionary<ZDO, ZNetView> instances))
            {
                snapshot.ownershipStatus = "field_unavailable";
                return;
            }
            snapshot.loadedObjects = instances.Count;
            var counts = new Dictionary<long, int>();
            int scanned = 0;
            foreach (var entry in instances)
            {
                if (scanned++ >= 10000) break;
                long owner = entry.Key.GetOwner();
                counts.TryGetValue(owner, out int count);
                counts[owner] = count + 1;
            }
            var owners = new List<OwnerCount>();
            foreach (var entry in counts)
                owners.Add(new OwnerCount { ownerSessionId = entry.Key.ToString(CultureInfo.InvariantCulture), objects = entry.Value });
            owners.Sort((a, b) => string.CompareOrdinal(a.ownerSessionId, b.ownerSessionId));
            snapshot.loadedOwnership = owners.ToArray();
            snapshot.ownershipStatus = instances.Count > 10000 ? "truncated_first_10000_loaded_objects" : "available";
        }
        catch (Exception ex) { snapshot.ownershipStatus = "unavailable:" + ex.GetType().Name; }
    }
}

internal static class TelemetryHooks
{
    internal static TelemetryCollector Collector;
    internal static TelemetryIntegration Integration;

    internal static void BeginNetworkUpdate(out long __state) => __state = Stopwatch.GetTimestamp();

    // Finalizers preserve the original exception and observe even failed updates.
    internal static Exception EndNetworkUpdate(Exception __exception, long __state)
    {
        Integration?.Observe("NetworkTiming", () => Collector?.NetworkUpdate((Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency));
        return __exception;
    }

    internal static void AfterZdoData(ZRpc __0) => Integration?.Observe("ZdoReceive", () => Collector?.ReceivedBatch(__0));
}
