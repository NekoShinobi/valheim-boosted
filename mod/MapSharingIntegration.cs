using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoosted;

internal sealed class MapSharingIntegration : IDisposable
{
    private const string RpcName = "valheim.boosted.map.v1";
    private static MapSharingIntegration current;
    private readonly NetworkFeaturePatch sharing, updates;
    private readonly ReplicationRelevance relevance;
    private sealed class Peer
    {
        internal ZNetPeer Connection; internal bool Capable, WantsUpdates, Acknowledged, Disabled;
        internal int Attempts, Invalid; internal double NextHello, NextAck, BudgetAt, Tokens = 8192;
    }
    private readonly Dictionary<ZRpc, Peer> peers = new Dictionary<ZRpc, Peer>();
    private readonly Dictionary<MapPlayerKey, MapPositionTrack> tracks = new Dictionary<MapPlayerKey, MapPositionTrack>();
    private ZNet net;
    private double nextTick, lastPositions;
    private long sequence, lastSequence;
    internal bool ServerRequiresSharing { get; private set; }
    internal long SentBytes, ReceivedBytes, Skipped, Rejected, PositionPackets;
    internal int CapablePeers => peers.Values.Count(p => p.Capable && p.WantsUpdates && !p.Disabled);
    internal bool ForceSharing => sharing.Status.Collect && net && net.IsDedicated();
    internal MapSharingIntegration(ConfigFile config, TelemetryIntegration integration, ReplicationRelevance relevance, Action<string> log)
    {
        this.relevance = relevance; current = this;
        sharing = new NetworkFeaturePatch("ForceMapSharing", "Map", "ForceLocationSharing", "Require public map positions for every player on this dedicated server, including unmodded clients; players keep their saved preference for other servers", config, integration, log);
        updates = new NetworkFeaturePatch("MapUpdates", "Map", "FastUpdates", "Send public positions at 2 Hz to clients that request this capability, and interpolate their map markers", config, integration, log);
        if (sharing.Status.Collect) try
        {
            sharing.Patch(NetworkEnhancementContracts.Method(typeof(ZNet), "UpdatePlayerList", typeof(void)), AccessTools.Method(typeof(MapSharingIntegration), nameof(TransformSharing)), true);
        } catch (Exception ex) { sharing.Fail(ex); }
        if (updates.Status.Collect) try
        {
            updates.Patch(NetworkEnhancementContracts.Method(typeof(ZNet), "GetOtherPublicPlayers", typeof(void), typeof(List<ZNet.PlayerInfo>)), AccessTools.Method(typeof(MapSharingIntegration), nameof(PublicPlayers)));
            updates.Patch(NetworkEnhancementContracts.Method(typeof(Minimap), "UpdatePlayerPins", typeof(void), typeof(float)), AccessTools.Method(typeof(MapSharingIntegration), nameof(TransformMarkers)), true);
        } catch (Exception ex) { updates.Fail(ex); }
    }
    private static bool Public(ZNetPeer peer)
    {
        bool forced = current != null && current.sharing.Status.Collect && ZNet.instance && ZNet.instance.IsDedicated();
        if (forced) current.sharing.Used();
        return forced || peer.m_publicRefPos;
    }
    private static IEnumerable<CodeInstruction> TransformSharing(IEnumerable<CodeInstruction> input) => NetworkEnhancementTransforms.ForceSharing(input, AccessTools.Method(typeof(MapSharingIntegration), nameof(Public)));
    private static IEnumerable<CodeInstruction> TransformMarkers(IEnumerable<CodeInstruction> input) => NetworkEnhancementTransforms.ReplaceCall(input,
        AccessTools.Method(typeof(Vector3), "MoveTowards", new[] { typeof(Vector3), typeof(Vector3), typeof(float) }), AccessTools.Method(typeof(MapSharingIntegration), nameof(MarkerPosition)));
    private bool Interpolating => updates.Status.Collect && net && !net.IsServer() && lastPositions > 0 && TelemetryCollector.Now - lastPositions <= 2;
    private static Vector3 MarkerPosition(Vector3 previous, Vector3 target, float maximumDelta)
        => current != null && current.Interpolating ? target : Vector3.MoveTowards(previous, target, maximumDelta);
    internal void Tick(double now)
    {
        if (now < nextTick) return; nextTick = now + .5;
        if (!ReferenceEquals(net, ZNet.instance)) { Clear(); net = ZNet.instance; }
        foreach (var feature in new[] { sharing, updates }) if (feature.Status.Collect)
            try { feature.Audit(); } catch (Exception ex) { feature.Fail(ex); }
        if (!net) return;
        if (!sharing.Status.Collect && !updates.Status.Collect) { Clear(); return; }
        var live = net.GetPeers();
        foreach (var rpc in peers.Where(p => !live.Contains(p.Value.Connection) || !p.Value.Connection.IsReady() || !p.Value.Connection.m_socket.IsConnected()).Select(p => p.Key).ToArray())
        {
            Remove(rpc);
            if (!net.IsServer()) { tracks.Clear(); lastSequence = 0; lastPositions = 0; ServerRequiresSharing = false; }
        }
        foreach (var p in live)
        {
            if (peers.ContainsKey(p.m_rpc) || !p.IsReady() || !p.m_socket.IsConnected() || !(p.m_socket is ZSteamSocket) || peers.Count >= 64) continue;
            peers[p.m_rpc] = new Peer { Connection = p, NextHello = now };
            p.m_rpc.Register<ZPackage>(RpcName, Message);
        }
        foreach (var state in peers.Values)
        {
            if (state.Disabled) continue;
            if (!net.IsServer() && ReferenceEquals(net.GetServerPeer(), state.Connection) && !state.Acknowledged && state.Attempts < 3 && now >= state.NextHello)
            {
                if (Send(state, new MapMessage { Kind = 1, Updates = updates.Status.Collect }))
                { state.Attempts++; state.NextHello = now + 5; }
            }
            if (net.IsDedicated() && state.Capable && now >= state.NextAck)
            {
                if (Send(state, new MapMessage { Kind = 2, Forced = ForceSharing, Updates = updates.Status.Collect && state.WantsUpdates }))
                { state.Acknowledged = true; state.NextAck = now + 10; }
            }
        }
        if (!net.IsDedicated() || !updates.Status.Collect || Game.m_noMap || !peers.Values.Any(p => p.Capable && p.WantsUpdates && !p.Disabled)) return;
        // A complete visibility set is required; never hide players by truncating it.
        if (live.Count(p => p.IsReady()) > MapPositionProtocol.MaxPlayers) { Skipped++; return; }
        var points = new List<MapPoint>();
        var keys = new HashSet<MapPlayerKey>();
        foreach (var p in live)
        {
            if (!p.IsReady() || !p.m_socket.IsConnected() || (!ForceSharing && !p.m_publicRefPos) || p.m_characterID.IsNone()) continue;
            var position = relevance.Position(p, p.m_refPos);
            var key = Key(p.m_characterID);
            if (!ReplicationRelevance.Finite(position) || Math.Abs(position.x) > 1000000 || Math.Abs(position.y) > 1000000 || Math.Abs(position.z) > 1000000 || !keys.Add(key)) continue;
            points.Add(new MapPoint { Key = key, X = position.x, Y = position.y, Z = position.z });
        }
        var message = new MapMessage { Kind = 3, Sequence = ++sequence, Updates = true, Forced = ForceSharing, Points = points.ToArray() };
        foreach (var state in peers.Values)
            if (state.Capable && state.Acknowledged && state.WantsUpdates && !state.Disabled && Send(state, message)) { PositionPackets++; updates.Used(); }
    }
    private static MapPlayerKey Key(ZDOID id) => new MapPlayerKey { User = id.UserID, Id = id.ID };
    private bool Send(Peer peer, MapMessage message)
    {
        try
        {
            // Optional map traffic yields to existing game traffic. No queued retries in the mod.
            if (!peer.Connection.m_socket.IsConnected() || peer.Connection.m_socket.GetSendQueueSize() > 10240) { Skipped++; return false; }
            var bytes = MapPositionProtocol.Encode(message);
            peer.Connection.m_rpc.Invoke(RpcName, new ZPackage(bytes)); SentBytes += bytes.Length; return true;
        }
        catch (Exception) { peer.Disabled = true; Rejected++; return false; }
    }
    private void Message(ZRpc rpc, ZPackage package)
    {
        if (!net || !peers.TryGetValue(rpc, out var state) || state.Disabled || !state.Connection.IsReady() || !state.Connection.m_socket.IsConnected()) return;
        double now = TelemetryCollector.Now;
        state.Tokens = Math.Min(8192, state.Tokens + Math.Max(0, now - state.BudgetAt) * 4096); state.BudgetAt = now;
        int size = package.Size();
        if (size > MapPositionProtocol.MaxBytes || size > state.Tokens || size < 14) { Reject(state); return; }
        state.Tokens -= size; ReceivedBytes += size;
        try
        {
            var message = MapPositionProtocol.Decode(package.GetArray());
            if (net.IsDedicated())
            {
                if (message.Kind != 1) { Reject(state); return; }
                state.Capable = true; state.WantsUpdates = message.Updates;
                // Repeated offers do not generate extra acknowledgements or position packets.
            }
            else if (!net.IsServer() && ReferenceEquals(net.GetServerPeer(), state.Connection))
            {
                if (message.Kind != 2 && message.Kind != 3) { Reject(state); return; }
                if (message.Kind == 3 && (!updates.Status.Collect || !state.Acknowledged || message.Sequence <= lastSequence)) { Reject(state); return; }
                ServerRequiresSharing = message.Forced;
                if (message.Kind == 2) { state.Acknowledged = true; if (!message.Updates) { tracks.Clear(); lastPositions = 0; } return; }
                lastSequence = message.Sequence; lastPositions = now;
                var present = new HashSet<MapPlayerKey>();
                foreach (var point in message.Points)
                {
                    present.Add(point.Key);
                    if (!tracks.TryGetValue(point.Key, out var track)) tracks[point.Key] = track = new MapPositionTrack();
                    track.Add(point, now);
                }
                foreach (var key in tracks.Keys.Where(k => !present.Contains(k)).ToArray()) tracks.Remove(key);
                updates.Used();
            }
        }
        catch (Exception) { Reject(state); }
    }
    private void Reject(Peer peer) { Rejected++; if (++peer.Invalid >= 5) { peer.Disabled = true; if (net && !net.IsServer()) { tracks.Clear(); lastPositions = 0; } } }
    private static void PublicPlayers(ZNet __instance, List<ZNet.PlayerInfo> playerList)
    {
        var self = current;
        if (self == null || !self.Interpolating || !ReferenceEquals(self.net, __instance)) return;
        for (int i = playerList.Count - 1; i >= 0; i--)
        {
            var player = playerList[i];
            if (!self.tracks.TryGetValue(Key(player.m_characterID), out var track)) { playerList.RemoveAt(i); continue; }
            var p = track.Value(TelemetryCollector.Now); player.m_position = new Vector3(p.X, p.Y, p.Z); playerList[i] = player;
        }
    }
    private void Remove(ZRpc rpc) { rpc.Unregister(RpcName); peers.Remove(rpc); }
    private void Clear() { foreach (var rpc in peers.Keys.ToArray()) Remove(rpc); tracks.Clear(); sequence = lastSequence = 0; lastPositions = 0; ServerRequiresSharing = false; }
    public void Dispose() { Clear(); sharing.Dispose(); updates.Dispose(); if (current == this) current = null; }
}
