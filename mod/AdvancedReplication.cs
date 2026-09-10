using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace ValheimBoosted;

internal sealed class AdvancedReplication : IDisposable
{
    private sealed class Peer
    {
        internal ZNetPeer Connection;
        internal readonly SendWindowPolicy Window = new SendWindowPolicy();
        internal readonly PeerImprovementMetrics Metrics = new PeerImprovementMetrics();
        internal SteamRateLease Rate;
        internal double Observed;
    }
    internal readonly FeatureStatus Windows, Rates, Compression;
    internal bool Patched { get; private set; }
    private readonly Dictionary<ZRpc, Peer> peers = new Dictionary<ZRpc, Peer>();
    private readonly int targetRate, maximumWindow, maximumRate;
    private readonly double compressionBudget;
    private readonly Action<string> log;
    private SteamRateAdapter rates;
    private ZdoCompression compression;
    private ZNet net;
    private double nextTick;
    internal AdvancedReplication(ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        this.log = log;
        Windows = Feature("SendWindows", config.Bind("SendWindows", "Enabled", true, "Experimental per-peer ZDO allowances on dedicated Steam servers. Restart required.").Value);
        Rates = Feature("SteamRate", config.Bind("SteamRate", "Enabled", true, "Experimentally raise connection SendRateMax; preserve globals and SendRateMin. Restart required.").Value);
        Compression = Feature("Compression", config.Bind("Compression", "Enabled", true, "Negotiate optional lossless ZDO compression with compatible Steam peers. Both endpoints must enable it. Restart required.").Value);
        Windows.target = "ZDOMan.SendZDOs"; Rates.target = "Steam connection SendRateMax"; Compression.target = "ZDOData (negotiated peers)";
        targetRate = config.Bind("SendWindows", "TargetBytesPerSecond", 153600, new ConfigDescription("Ceiling for RTT-based allowance calculations, in bytes/second.", new AcceptableValueRange<int>(10240, 1048576))).Value;
        maximumWindow = config.Bind("SendWindows", "MaximumBytes", 32768, new ConfigDescription("Maximum per-peer ZDO allowance, in bytes. Vanilla floor is 10240.", new AcceptableValueRange<int>(10240, 65536))).Value;
        maximumRate = config.Bind("SteamRate", "MaximumBytesPerSecond", 307200, new ConfigDescription("Requested per-connection SendRateMax in bytes/second. Never lowers an existing greater limit or changes SendRateMin.", new AcceptableValueRange<int>(10240, 1048576))).Value;
        compressionBudget = config.Bind("Compression", "EncodeBudgetMs", 1f, new ConfigDescription("Soft total encoding budget per frame in milliseconds. One bounded 64 KiB payload is indivisible; remaining sends use vanilla.", new AcceptableValueRange<float>(0.1f, 5f))).Value;
        FeatureStatus Feature(string id, bool enabled)
        {
            var f = new FeatureStatus { id = id, enabled = enabled, status = !enabled ? "configured_disabled" : integration.GameSupported ? "available" : "blocked_compatibility" };
            integration.Features[id] = f; return f;
        }
    }
    internal bool NeedsPatch => Windows.Collect || Compression.Collect;
    internal void Initialize()
    {
        if (Windows.Collect)
            try { SteamMetrics.Initialize(); }
            catch (Exception ex) { Fail(Windows, "contract_mismatch", ex.Message); }
        if (Rates.Collect)
            try { rates = new SteamRateAdapter(); }
            catch (Exception ex) { Fail(Rates, "contract_mismatch", ex.Message); }
        if (Compression.Collect)
            try { compression = new ZdoCompression(Compression, compressionBudget, log); }
            catch (Exception ex) { Fail(Compression, "contract_mismatch", ex.Message); }
    }
    internal void MarkPatched() { Patched = true; if (Windows.Collect) Windows.status = "installed_waiting"; if (Compression.Collect) Compression.status = "installed_waiting"; }
    internal void Tick(double now)
    {
        // Receive-drain handlers must remain alive after the sending path falls back.
        compression?.Tick(now);
        if (now < nextTick) return; nextTick = now + 1;
        if (!ReferenceEquals(net, ZNet.instance)) { RestoreAll(); peers.Clear(); net = ZNet.instance; }
        if (!net || !net.IsDedicated() || (!Windows.Collect && !Rates.Collect)) return;
        var live = net.GetPeers();
        foreach (var rpc in peers.Where(p => !live.Contains(p.Value.Connection) || !p.Value.Connection.IsReady() || !p.Value.Connection.m_socket.IsConnected()).Select(p => p.Key).ToArray())
        { Restore(peers[rpc]); peers.Remove(rpc); }
        foreach (var connection in live)
        {
            if (!connection.IsReady() || !connection.m_socket.IsConnected() || !(connection.m_socket is ZSteamSocket socket)) continue;
            if (!peers.TryGetValue(connection.m_rpc, out var peer))
            {
                if (peers.Count >= 64) break;
                peers[connection.m_rpc] = peer = new Peer { Connection = connection };
                if (rates != null) peer.Rate = new SteamRateLease(max => rates.Read(socket, max), value => rates.WriteMaximum(socket, value), peer.Metrics);
            }
            var m = peer.Metrics;
            if (Rates.Collect && peer.Rate != null)
            {
                int failures = m.rateWriteFailures;
                if (peer.Rate.Tick(maximumRate)) { Rates.status = "active"; Rates.invocations++; }
                if (m.rateWriteFailures != failures) { Rates.status = "degraded"; Rates.detail = peer.Rate.Error; log("Steam connection rate: " + peer.Rate.Error); }
            }
            if (Windows.Collect)
            {
                var sample = new PeerMetrics();
                try { SteamMetrics.ReadManagedQueue(socket, sample); SteamMetrics.Read(socket, sample); }
                catch { sample.measurementStatus = "unavailable"; }
                m.windowBytes = peer.Window.Observe(sample.measurementStatus == "available", sample.rttMs, sample.estimatedTransportQueueMs,
                    sample.applicationQueuedBytes, sample.pendingReliableBytes, sample.estimatedSendRateBytesPerSecond, targetRate, maximumWindow);
                m.windowStatus = peer.Window.Reason; peer.Observed = now;
            }
        }
    }
    private void Restore(Peer peer)
    {
        if (!peer.Connection.m_socket.IsConnected()) return;
        peer.Rate?.Restore();
        if (peer.Metrics.rateStatus == "restore_failed") log("Steam connection restoration: " + peer.Rate.Error);
    }
    private void RestoreAll() { foreach (var peer in peers.Values) Restore(peer); }
    internal int Window(ZNetPeer peer, bool flush)
    {
        if (flush || !Patched || !Windows.Collect || !ZNet.instance || !ZNet.instance.IsDedicated() || !(peer.m_socket is ZSteamSocket)
            || !peers.TryGetValue(peer.m_rpc, out var state) || !ReferenceEquals(state.Connection, peer) || TelemetryCollector.Now - state.Observed > 3)
            return SendWindowPolicy.VanillaBytes;
        Windows.invocations++; Windows.status = "active"; return state.Window.Bytes;
    }
    internal void Invoke(ZRpc rpc, string method, object[] args) { if (compression == null) rpc.Invoke(method, args); else compression.Invoke(rpc, method, args); }
    internal void Capture(TelemetrySnapshot snapshot)
    {
        snapshot.serverImprovements = new ServerImprovementMetrics { windowsEnabled = Windows.enabled, rateEnabled = Rates.enabled, compressionEnabled = Compression.enabled,
            targetBytesPerSecond = targetRate, maximumWindowBytes = maximumWindow, requestedMaxRateBytesPerSecond = maximumRate, compressionBudgetMs = compressionBudget, steamConfigInterface = rates?.Backend };
        compression?.Capture(snapshot.serverImprovements);
        foreach (var p in snapshot.peers ?? new PeerMetrics[0])
        {
            var state = peers.Values.FirstOrDefault(v => v.Connection.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture) == p.peerSessionId);
            var m = state == null ? new PeerImprovementMetrics() : Copy(state.Metrics);
            if (!Windows.Collect) { m.windowBytes = SendWindowPolicy.VanillaBytes; m.windowStatus = Windows.status; }
            else if (state == null || TelemetryCollector.Now - state.Observed > 3) { m.windowBytes = SendWindowPolicy.VanillaBytes; m.windowStatus = "vanilla_no_fresh_measurement"; }
            if (!Rates.Collect) m.rateStatus = Rates.status;
            m.compressionStatus = compression?.Status(ZNet.instance?.GetPeer(long.Parse(p.peerSessionId))?.m_rpc) ?? Compression.status;
            p.improvements = m;
        }
    }
    private static PeerImprovementMetrics Copy(PeerImprovementMetrics m) => new PeerImprovementMetrics {
        windowBytes = m.windowBytes, windowStatus = m.windowStatus, rateStatus = m.rateStatus, originalMaxRateBytesPerSecond = m.originalMaxRateBytesPerSecond,
        effectiveMaxRateBytesPerSecond = m.effectiveMaxRateBytesPerSecond, minimumRateBytesPerSecond = m.minimumRateBytesPerSecond, rateWriteFailures = m.rateWriteFailures };
    private static void Fail(FeatureStatus feature, string status, string reason) { if (feature.Collect) { feature.status = status; feature.detail = reason; } }
    internal void Disable(string reason)
    {
        Patched = false; RestoreAll(); compression?.StopSending(reason);
        Fail(Windows, "runtime_failed", reason); Fail(Rates, "runtime_failed", reason); Fail(Compression, "runtime_failed", reason);
    }
    public void Dispose() { RestoreAll(); peers.Clear(); compression?.Dispose(); }
}
