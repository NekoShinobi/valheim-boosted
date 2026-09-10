using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class EarlyZdoIntegration : IDisposable
{
    internal static EarlyZdoIntegration Current;
    private sealed class State
    {
        internal ZNet Net; internal ZNetPeer Peer; internal ZDOMan Manager;
        internal object Wrapper, Vanilla;
        internal readonly EarlyZdoQueue Queue = new EarlyZdoQueue();
    }
    private readonly Dictionary<ZRpc, State> peers = new Dictionary<ZRpc, State>();
    private readonly NetworkFeaturePatch feature;
    private readonly Action<string> log;
    private MethodInfo receive;
    private FieldInfo functions;
    private readonly int dataHash = "ZDOData".GetStableHashCode();
    private double nextAudit;
    internal long Buffered, Replayed, Failures;
    internal int QueuedBytes => peers.Values.Sum(p => p.Queue.Bytes);
    internal EarlyZdoIntegration(ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        this.log = log; Current = this;
        feature = new NetworkFeaturePatch("EarlyZdoBuffer", "Replication", "EarlyZdoBuffer", "Buffer early server ZDO packets until the client replication peer is ready, with bounded FIFO replay", config, integration, log);
        if (!feature.Status.Collect) return;
        try
        {
            receive = NetworkEnhancementContracts.Receive(); feature.Dependency(receive, Plugin.PluginGuid);
            functions = AccessTools.Field(typeof(ZRpc), "m_functions");
            if (functions == null || !typeof(IDictionary).IsAssignableFrom(functions.FieldType)) throw new InvalidOperationException("RPC registration table changed");
            feature.Patch(NetworkEnhancementContracts.Method(typeof(ZNet), "OnNewConnection", typeof(void), typeof(ZNetPeer)), AccessTools.Method(typeof(EarlyZdoIntegration), nameof(Connected)));
            feature.Patch(NetworkEnhancementContracts.Method(typeof(ZDOMan), "AddPeer", typeof(void), typeof(ZNetPeer)), AccessTools.Method(typeof(EarlyZdoIntegration), nameof(Added)));
        }
        catch (Exception ex) { feature.Fail(ex); }
    }
    private IDictionary Table(ZRpc rpc) => (IDictionary)functions.GetValue(rpc);
    private static void Connected(ZNet __instance, ZNetPeer peer)
    {
        var self = Current;
        if (self == null || !self.feature.Status.Collect || __instance.IsServer() || !peer.m_server) return;
        try
        {
            if (self.peers.Count >= 1) return; // A client has a single server connection.
            if (self.Table(peer.m_rpc).Contains(self.dataHash)) throw new InvalidOperationException("An early ZDO handler is already registered");
            var state = new State { Net = __instance, Peer = peer };
            self.peers[peer.m_rpc] = state;
            self.Register(state);
        }
        catch (Exception ex) { self.Stop(ex); }
    }
    private void Register(State state)
    {
        state.Peer.m_rpc.Register<ZPackage>("ZDOData", Receive);
        state.Wrapper = Table(state.Peer.m_rpc)[dataHash];
    }
    private static void Added(ZDOMan __instance, ZNetPeer netPeer)
    {
        var self = Current;
        if (self == null || !self.peers.TryGetValue(netPeer.m_rpc, out var state)) return;
        try
        {
            state.Manager = __instance;
            state.Vanilla = self.Table(netPeer.m_rpc)[self.dataHash];
            var action = state.Vanilla == null ? null : AccessTools.Field(state.Vanilla.GetType(), "m_action")?.GetValue(state.Vanilla) as Delegate;
            if (action?.Method != self.receive || !ReferenceEquals(action.Target, __instance)) throw new InvalidOperationException("ZDO handler changed during peer registration");
            if (state.Queue.Count == 0) self.peers.Remove(netPeer.m_rpc);
            else self.Register(state); // Do not let live packets overtake queued packets after AddPeer.
        }
        catch (Exception ex) { self.Stop(ex); }
    }
    private void Receive(ZRpc rpc, ZPackage package)
    {
        if (!peers.TryGetValue(rpc, out var state)) return;
        Buffer(state, package);
    }
    private void Buffer(State state, ZPackage package)
    {
        double now = TelemetryCollector.Now;
        try
        {
            if (!state.Queue.CanAdd(package.Size(), now)) { Close(state, "Early ZDO queue size or age limit"); return; }
            // RPC payloads enter at position zero; copy before the game's receive buffer is reused.
            if (package.GetPos() != 0) { Close(state, "Unexpected early ZDO read position"); return; }
            state.Queue.Add((byte[])package.GetArray().Clone(), now); Buffered++; feature.Used();
        }
        catch (Exception ex) { Close(state, ex.GetBaseException().Message); }
    }
    // Compression uses this same FIFO before invoking the vanilla receiver.
    internal bool BufferCompressed(ZRpc rpc, ZPackage package)
    {
        if (!peers.TryGetValue(rpc, out var state)) return false;
        Buffer(state, package); return true;
    }
    internal void Tick(double now)
    {
        if (!feature.Status.Collect) return;
        try
        {
            if (now >= nextAudit) { nextAudit = now + 1; feature.Audit(); }
            foreach (var state in peers.Values.ToArray())
            {
                if (!ReferenceEquals(state.Net, ZNet.instance) || !state.Net.GetPeers().Contains(state.Peer) || !state.Peer.m_socket.IsConnected())
                { Remove(state); continue; }
                if (!ReferenceEquals(Table(state.Peer.m_rpc)[dataHash], state.Wrapper)) throw new InvalidOperationException("Early ZDO handler was replaced");
                if (state.Queue.Expired(now)) { Close(state, "Early ZDO queue timed out"); continue; }
                if (state.Manager == null || !state.Peer.IsReady()) continue;
                if (!ReferenceEquals(state.Manager, ZDOMan.instance)) { Close(state, "World changed during ZDO replay"); continue; }
                double start = TelemetryCollector.Now;
                int calls = 0;
                // Soft 1 ms budget, at most four complete vanilla packets. Never split a packet's state updates.
                while (state.Queue.Count > 0 && calls++ < 4 && TelemetryCollector.Now - start < .001)
                {
                    try { receive.Invoke(state.Manager, new object[] { state.Peer.m_rpc, new ZPackage(state.Queue.Take()) }); Replayed++; }
                    catch (Exception ex) { Close(state, "Early ZDO replay failed: " + ex.GetBaseException().Message); break; }
                }
                if (state.Queue.Count == 0) Remove(state);
            }
        }
        catch (Exception ex) { Stop(ex); }
    }
    private void Remove(State state)
    {
        var table = Table(state.Peer.m_rpc);
        if (ReferenceEquals(table[dataHash], state.Wrapper))
        { if (state.Vanilla == null) table.Remove(dataHash); else table[dataHash] = state.Vanilla; }
        peers.Remove(state.Peer.m_rpc); state.Queue.Clear();
    }
    private void Close(State state, string reason)
    { Failures++; log(reason + "; reconnect required to avoid losing committed world state"); state.Peer.m_socket.Close(); Remove(state); }
    private void Stop(Exception ex)
    {
        foreach (var state in peers.Values.ToArray())
        { if (state.Queue.Count > 0) Close(state, "Early ZDO compatibility changed"); else Remove(state); }
        feature.Fail(ex);
    }
    public void Dispose()
    {
        foreach (var state in peers.Values.ToArray())
        { if (state.Queue.Count > 0) Close(state, "Early ZDO buffer unloaded"); else Remove(state); }
        feature.Dispose(); if (Current == this) Current = null;
    }
}
