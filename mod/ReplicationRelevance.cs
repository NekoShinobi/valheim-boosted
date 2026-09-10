using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoosted;

internal sealed class ReplicationRelevance : IDisposable
{
    private static ReplicationRelevance current;
    private readonly NetworkFeaturePatch interest, priority;
    private readonly Dictionary<ZNetPeer, PositionState> positions = new Dictionary<ZNetPeer, PositionState>();
    private readonly Dictionary<int, int> categories = new Dictionary<int, int>();
    private readonly Dictionary<object, long> passes = new Dictionary<object, long>();
    private sealed class PositionState { internal ZDOID Id; internal CharacterFreshness Fresh = new CharacterFreshness(); }
    private ZNet net;
    private ZNetScene scene;
    private double nextTick;
    private FieldInfo peerField;
    internal long FreshPositions, PositionFallbacks, ActorBonuses, VanillaPasses;
    internal ReplicationRelevance(ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        current = this;
        interest = new NetworkFeaturePatch("FreshInterest", "Replication", "FreshInterest", "Use a recent character position only when it is near the peer's normal reference position; never change simulation ownership", config, integration, log);
        priority = new NetworkFeaturePatch("ActorPriority", "Replication", "ActorPriority", "Give actors a bounded send priority inside vanilla tiers, with every fourth pass reserved for vanilla ordering", config, integration, log);
        if (interest.Status.Collect) try
        {
            interest.Dependency(NetworkEnhancementContracts.Method(typeof(ZNetPeer), "GetRefPos", typeof(Vector3)));
            interest.Patch(NetworkEnhancementContracts.SyncList(), AccessTools.Method(typeof(ReplicationRelevance), nameof(TransformInterest)), true);
        } catch (Exception ex) { interest.Fail(ex); }
        if (priority.Status.Collect) try
        {
            peerField = AccessTools.Field(NetworkEnhancementContracts.PeerType, "m_peer");
            if (peerField?.FieldType != typeof(ZNetPeer)) throw new InvalidOperationException("Priority peer field changed");
            priority.Dependency(NetworkEnhancementContracts.Method(typeof(ZDOMan), "ServerSendCompare", typeof(int), typeof(ZDO), typeof(ZDO)));
            priority.Dependency(NetworkEnhancementContracts.Method(typeof(ZDOMan), "AddForceSendZdos", typeof(void), NetworkEnhancementContracts.PeerType, typeof(List<ZDO>)));
            priority.Patch(NetworkEnhancementContracts.Sort(), AccessTools.Method(typeof(ReplicationRelevance), nameof(TransformPriority)), true);
        } catch (Exception ex) { priority.Fail(ex); }
    }
    internal void Tick(double now)
    {
        if (now < nextTick) return; nextTick = now + .25;
        if (!ReferenceEquals(net, ZNet.instance) || !ReferenceEquals(scene, ZNetScene.instance))
        { net = ZNet.instance; scene = ZNetScene.instance; positions.Clear(); categories.Clear(); passes.Clear(); }
        Audit();
        if (!net || !net.IsDedicated() || ZDOMan.instance == null) return;
        var live = net.GetPeers();
        foreach (var key in passes.Keys.Where(p => !(peerField?.GetValue(p) is ZNetPeer connection) || !live.Contains(connection) || !connection.IsReady()).ToArray()) passes.Remove(key);
        foreach (var peer in positions.Keys.Where(p => !live.Contains(p) || !p.IsReady()).ToArray()) positions.Remove(peer);
        foreach (var peer in live)
        {
            if (!peer.IsReady() || !(peer.m_socket is ZSteamSocket) || !peer.m_socket.IsConnected()) continue;
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (zdo == null || zdo.GetOwner() != peer.m_uid) { positions.Remove(peer); continue; }
            if (!positions.TryGetValue(peer, out var state) || state.Id != peer.m_characterID)
            {
                if (positions.Count >= 64) continue;
                positions[peer] = state = new PositionState { Id = peer.m_characterID };
            }
            state.Fresh.Observe(zdo.DataRevision, now);
        }
    }
    internal void Audit()
    {
        foreach (var feature in new[] { interest, priority }) if (feature.Status.Collect)
            try { feature.Audit(); } catch (Exception ex) { feature.Fail(ex); }
    }
    internal Vector3 Position(ZNetPeer peer, Vector3 fallback)
    {
        if (positions.TryGetValue(peer, out var state) && state.Id == peer.m_characterID && state.Fresh.Fresh(TelemetryCollector.Now))
        {
            var zdo = ZDOMan.instance?.GetZDO(state.Id);
            if (zdo != null && zdo.GetOwner() == peer.m_uid)
            {
                var value = zdo.GetPosition();
                // Custom camera/reference modes, teleports and implausible coordinates retain vanilla relevance.
                if (Finite(value) && Finite(fallback) && (value - fallback).sqrMagnitude <= 32 * 32) return value;
            }
        }
        return fallback;
    }
    internal static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
        || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
    private static Vector3 Reference(ZNetPeer peer)
    {
        var fallback = peer.GetRefPos(); var self = current;
        if (self == null || !self.interest.Status.Collect || !ZNet.instance || !ZNet.instance.IsDedicated()) return fallback;
        try
        {
            var position = self.Position(peer, fallback);
            if (position != fallback) { self.FreshPositions++; self.interest.Used(); } else self.PositionFallbacks++;
            return position;
        }
        catch (Exception ex) { self.interest.Fail(ex); return fallback; }
    }
    private int Category(ZDO zdo)
    {
        int hash = zdo.GetPrefab();
        if (categories.TryGetValue(hash, out int kind)) return kind;
        if (!scene || categories.Count >= 4096) return 0;
        var prefab = scene.GetPrefab(hash);
        if (!prefab) return 0; // A late prefab registration can become available later.
        kind = prefab.GetComponent<Player>() ? 3 : prefab.GetComponent<Ship>() ? 2 : prefab.GetComponent<Character>() ? 1 : 0;
        categories[hash] = kind; return kind;
    }
    private static void Sort(List<ZDO> objects, Comparison<ZDO> comparison, object peer)
    {
        var self = current;
        if (self != null && self.priority.Status.Collect && ZNet.instance && ZNet.instance.IsDedicated())
        {
            // Adjust before the game's single sort. The original comparator and forced-send insertion remain intact.
            try
            {
                var connection = self.peerField.GetValue(peer) as ZNetPeer;
                if (connection?.m_socket is ZSteamSocket && (self.passes.ContainsKey(peer) || self.passes.Count < 64))
                {
                    self.passes.TryGetValue(peer, out long pass); self.passes[peer] = ++pass;
                    if (ActorPriorityPolicy.Boost(pass))
                    {
                        // Resolve prefab components before changing any scores so a lookup failure keeps vanilla ordering.
                        foreach (var zdo in objects) self.Category(zdo);
                        foreach (var zdo in objects)
                        {
                            self.categories.TryGetValue(zdo.GetPrefab(), out int category);
                            float bonus = ActorPriorityPolicy.Bonus(category);
                            if (bonus == 0) continue;
                            zdo.m_tempSortValue -= bonus; self.ActorBonuses++;
                        }
                    }
                    else self.VanillaPasses++;
                    self.priority.Used();
                }
            }
            catch (Exception ex) { self.priority.Fail(ex); }
        }
        objects.Sort(comparison);
    }
    private static IEnumerable<CodeInstruction> TransformInterest(IEnumerable<CodeInstruction> input) => NetworkEnhancementTransforms.ReplaceCall(input,
        AccessTools.Method(typeof(ZNetPeer), "GetRefPos"), AccessTools.Method(typeof(ReplicationRelevance), nameof(Reference)));
    private static IEnumerable<CodeInstruction> TransformPriority(IEnumerable<CodeInstruction> input)
    {
        // Stack holds List<ZDO>, Comparison<ZDO>; supply the per-peer fairness key as the third argument.
        var result = input.Select(i => new CodeInstruction(i)).ToList();
        var original = AccessTools.Method(typeof(List<ZDO>), "Sort", new[] { typeof(Comparison<ZDO>) });
        var sites = result.Select((i, n) => new { i, n }).Where(x => x.i.Calls(original)).ToArray();
        if (sites.Length != 1) throw new InvalidOperationException("Expected one vanilla server sort");
        int at = sites[0].n;
        var load = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_3);
        load.labels.AddRange(result[at].labels); result[at].labels.Clear();
        load.blocks.AddRange(result[at].blocks); result[at].blocks.Clear();
        result[at].opcode = System.Reflection.Emit.OpCodes.Call; result[at].operand = AccessTools.Method(typeof(ReplicationRelevance), nameof(Sort));
        result.Insert(at, load); return result;
    }
    public void Dispose() { interest.Dispose(); priority.Dispose(); positions.Clear(); categories.Clear(); passes.Clear(); if (current == this) current = null; }
}
