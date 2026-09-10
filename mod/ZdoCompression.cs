using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class ZdoCompression : IDisposable
{
    private const string ControlRpc = "valheim.boosted.compression.v1", DataRpc = "valheim.boosted.zdo.v1";
    private sealed class Peer
    {
        internal ZNetPeer Connection;
        internal readonly CompressionSession Session = new CompressionSession();
        internal double NextOffer;
        internal string Status = "negotiating";
    }
    private readonly Dictionary<ZRpc, Peer> peers = new Dictionary<ZRpc, Peer>();
    private readonly FeatureStatus feature;
    private readonly Action<string> log;
    private readonly double budget;
    private readonly MethodInfo receive;
    private readonly SampleWindow encodeTime = new SampleWindow(), decodeTime = new SampleWindow();
    private ZNet net;
    private double nextTick, frameWork;
    private int frame = -1;
    private bool sending = true;
    private long rawBytes, frameBytes, sent, received, skipped, rejected;
    internal ZdoCompression(FeatureStatus feature, double budget, Action<string> log)
    {
        this.feature = feature; this.budget = budget; this.log = log;
        receive = typeof(ZDOMan).GetMethod("RPC_ZDOData", BindingFlags.NonPublic | BindingFlags.Instance);
        if (feature.Collect && (!CompatibilityPolicy.Signature(receive, typeof(ZDOMan), typeof(void), typeof(ZRpc), typeof(ZPackage))
            || !CompatibilityPolicy.MatchesFingerprint(CompatibilityPolicy.ReceiveHash, CompatibilityPolicy.Fingerprint(receive))))
            throw new InvalidOperationException("Compression requires the reviewed vanilla ZDO receiver");
        if (ForeignReceiver()) throw new InvalidOperationException("Another mod patches the ZDO receiver");
    }
    private bool ForeignReceiver() => Harmony.GetPatchInfo(receive)?.Owners.Any(owner => owner != Plugin.PluginGuid) == true;
    internal void Tick(double now)
    {
        if (now < nextTick) return; nextTick = now + 1;
        if (sending && feature.Collect && ForeignReceiver()) StopSending("receiver_patch_changed");
        if (!ReferenceEquals(net, ZNet.instance)) { Clear(); net = ZNet.instance; }
        if (!net) return;
        var live = net.GetPeers();
        foreach (var rpc in peers.Where(p => !live.Contains(p.Value.Connection) || !p.Value.Connection.IsReady() || !p.Value.Connection.m_socket.IsConnected()).Select(p => p.Key).ToArray()) Remove(rpc);
        if (!feature.Collect || !sending) return;
        foreach (var p in live)
        {
            if (!p.IsReady() || !p.m_socket.IsConnected() || !(p.m_socket is ZSteamSocket)) continue;
            if (!peers.TryGetValue(p.m_rpc, out var state))
            {
                if (peers.Count >= 64) break;
                peers[p.m_rpc] = state = new Peer { Connection = p, NextOffer = now + peers.Count % 3 };
                p.m_rpc.Register<ZPackage>(ControlRpc, Control); p.m_rpc.Register<ZPackage>(DataRpc, Data);
            }
            if (!state.Session.SendReady && !state.Session.Stopped && state.Session.Attempts < 3 && now >= state.NextOffer)
            {
                state.NextOffer = now + 10;
                p.m_rpc.Invoke(ControlRpc, new ZPackage(state.Session.Offer()));
            }
            state.Status = state.Session.Stopped ? "receive_drain" : state.Session.SendReady ? "negotiated" : state.Session.Attempts >= 3 ? "vanilla_no_agreement" : "negotiating";
        }
    }
    private void Control(ZRpc rpc, ZPackage package)
    {
        if (!peers.TryGetValue(rpc, out var peer) || !peer.Connection.IsReady()) return;
        try
        {
            if (package.Size() != 29) throw new System.IO.InvalidDataException("Invalid compression negotiation length");
            var response = peer.Session.Receive(package.GetArray());
            if (response != null) rpc.Invoke(ControlRpc, new ZPackage(response));
        }
        catch (Exception) { rejected++; peer.Status = "vanilla_negotiation_rejected"; }
    }
    private void Data(ZRpc rpc, ZPackage package)
    {
        if (!peers.TryGetValue(rpc, out var peer) || !peer.Connection.IsReady()) return;
        byte[] raw; double started = TelemetryCollector.Now;
        try
        {
            if (package.Size() > LosslessZdoCodec.MaximumRawBytes + 16) throw new System.IO.InvalidDataException("Compressed payload too large");
            raw = peer.Session.Decode(package.GetArray());
        }
        catch (Exception ex)
        {
            rejected++; peer.Status = "invalid_compressed_payload";
            // Never feed malformed framed bytes to the vanilla parser or silently lose committed ZDO state.
            log("Compressed ZDO rejected; closing this connection: " + ex.Message);
            peer.Connection.m_socket.Close(); return;
        }
        finally { decodeTime.Add((TelemetryCollector.Now - started) * 1000); }
        received++; feature.invocations++; if (feature.Collect) feature.status = "active";
        var manager = ZDOMan.instance;
        if (manager != null)
        {
            var decoded = new ZPackage(raw);
            if (EarlyZdoIntegration.Current?.BufferCompressed(rpc, decoded) != true)
                receive.Invoke(manager, new object[] { rpc, decoded });
        }
    }
    internal void Invoke(ZRpc rpc, string method, object[] args)
    {
        byte[] output = null;
        if (sending && feature.Collect && method == "ZDOData" && args.Length == 1 && args[0] is ZPackage package
            && peers.TryGetValue(rpc, out var peer) && peer.Session.SendReady && !peer.Session.Stopped)
        {
            if (frame != Time.frameCount) { frame = Time.frameCount; frameWork = 0; }
            if (frameWork < budget && package.Size() >= 512 && package.Size() <= LosslessZdoCodec.MaximumRawBytes)
            {
                double started = TelemetryCollector.Now;
                try
                {
                    var encoded = LosslessZdoCodec.Encode(package.GetArray());
                    if (encoded != null) { output = peer.Session.Frame(encoded); rawBytes += package.Size(); frameBytes += output.Length; sent++; feature.invocations++; feature.status = "active"; }
                }
                catch (Exception ex) { StopSending("encode_failed"); log("Compression send fallback: " + ex.Message); }
                finally { double elapsed = (TelemetryCollector.Now - started) * 1000; frameWork += elapsed; encodeTime.Add(elapsed); }
            }
            if (output == null) skipped++;
        }
        // One dispatch, after encoding is complete. Steam's existing queue retries these immutable bytes.
        if (output != null) rpc.Invoke(DataRpc, new ZPackage(output));
        else rpc.Invoke(method, args);
    }
    internal void StopSending(string reason)
    {
        sending = false;
        foreach (var peer in peers.Values)
        {
            peer.Status = "receive_drain";
            if (!peer.Session.Stopped && peer.Connection.m_socket.IsConnected())
                try { peer.Connection.m_rpc.Invoke(ControlRpc, new ZPackage(peer.Session.Stop())); } catch { /* Socket failure is handled by the game. */ }
        }
        if (feature.Collect) { feature.status = "runtime_failed"; feature.detail = reason + "; queued receive frames remain decodable until disconnect"; }
    }
    internal string Status(ZRpc rpc) => rpc != null && peers.TryGetValue(rpc, out var p) ? p.Status : feature.enabled ? "vanilla_not_negotiated" : "configured_disabled";
    internal void Capture(ServerImprovementMetrics result)
    {
        result.rawPayloadBytes = rawBytes; result.framedPayloadBytes = frameBytes; result.compressedSent = sent; result.compressedReceived = received;
        result.compressionSkipped = skipped; result.compressionRejected = rejected; result.compressionEncodeMs = encodeTime.Take(); result.compressionDecodeMs = decodeTime.Take();
        rawBytes = frameBytes = sent = received = skipped = rejected = 0;
    }
    private void Remove(ZRpc rpc) { rpc.Unregister(ControlRpc); rpc.Unregister(DataRpc); peers.Remove(rpc); }
    private void Clear() { foreach (var rpc in peers.Keys.ToArray()) Remove(rpc); }
    public void Dispose()
    {
        // Hot-unloading a decoder cannot leave peers sending frames to an absent handler.
        foreach (var p in peers.Values) if (p.Session.ReceiveReady && p.Connection.m_socket.IsConnected()) p.Connection.m_socket.Close();
        Clear();
    }
}
