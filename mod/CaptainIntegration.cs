using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimBoosted;

// Read replicated captain grants and character attachment state; never instantiate distant ships.
internal sealed class CaptainIntegration
{
    private readonly FeatureStatus feature;
    private readonly Action<string> log;
    private sealed class State { internal readonly CaptainPolicy Policy = new CaptainPolicy(); internal double Seen; }
    private readonly Dictionary<ZDOID, State> policies = new Dictionary<ZDOID, State>();
    private readonly Dictionary<int, Vector3?> helms = new Dictionary<int, Vector3?>();
    private CaptainContracts contracts;
    private ZNet net;
    private double nextTick;
    private int candidates, transfers, deferred;
    internal CaptainIntegration(ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        this.log = log;
        bool enabled = config.Bind("CaptainOwnership", "Enabled", true, "Experimental dedicated Steam server assignment of vanilla ships to their granted, attached captain. Restart required.").Value;
        feature = new FeatureStatus { id = "CaptainOwnership", enabled = enabled, status = !enabled ? "configured_disabled" : integration.GameSupported ? "available" : "blocked_compatibility" };
        integration.Features[feature.id] = feature;
        feature.target = "ZDO.SetOwner (granted ship captain)";
        if (feature.Collect) try { contracts = new CaptainContracts(); } catch (Exception ex) { Fail("contract_mismatch", ex); }
    }
    internal void Tick(double now)
    {
        if (!feature.Collect || now < nextTick) return; nextTick = now + 1;
        try
        {
            contracts.Audit();
            if (!ReferenceEquals(net, ZNet.instance)) { net = ZNet.instance; policies.Clear(); helms.Clear(); }
            candidates = 0;
            if (!net || !net.IsDedicated() || !ZNetScene.instance || !ZoneSystem.instance || ZDOMan.instance == null)
            { feature.status = "installed_waiting"; return; }
            var live = new Dictionary<ZDOID, ZNetPeer>(); int changed = 0, scanned = 0;
            foreach (var peer in net.GetPeers())
            {
                if (++scanned > 64) break;
                if (!peer.IsReady() || !(peer.m_socket is ZSteamSocket) || !peer.m_socket.IsConnected()) continue;
                var player = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (player == null || player.GetOwner() != peer.m_uid) continue;
                var id = player.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform);
                if (id == ZDOID.None) continue;
                var ship = ZDOMan.instance.GetZDO(id);
                if (ship == null || !Helm(ship.GetPrefab(), out var helm)) continue;
                var relative = player.GetVec3(ZDOVars.s_relPosHash, new Vector3(float.NaN, float.NaN, float.NaN));
                bool eligible = CaptainPolicy.Eligible(peer.m_uid, player.GetOwner(), player.GetLong(ZDOVars.s_playerID), ship.GetLong(ZDOVars.s_user),
                    (relative - helm).sqrMagnitude, ZNetScene.InActiveArea(ship.GetPosition(), peer.m_refPos));
                if (!eligible) continue;
                if (live.ContainsKey(id)) live[id] = null;
                else live[id] = peer;
            }
            foreach (var entry in live)
            {
                if (entry.Value == null) continue;
                var id = entry.Key; var peer = entry.Value; var ship = ZDOMan.instance.GetZDO(id);
                candidates++;
                if (!policies.TryGetValue(id, out var state))
                {
                    if (policies.Count >= 128) { deferred++; continue; }
                    policies[id] = state = new State();
                }
                state.Seen = now;
                if (!state.Policy.ShouldTransfer(ship.GetOwner(), peer.m_uid, true, now)) continue;
                if (changed >= 2) { deferred++; continue; }
                ship.SetOwner(peer.m_uid);
                state.Policy.Transferred(now); changed++; transfers++; feature.invocations++;
                ZDOMan.instance.ForceSendZDO(id);
            }
            // Retain the cooldown across brief dismounts; missing/ambiguous captains reset the dwell.
            foreach (var entry in policies)
                if (!live.TryGetValue(entry.Key, out var peer) || peer == null) entry.Value.Policy.ShouldTransfer(0, 0, false, now);
            foreach (var id in policies.Where(p => now - p.Value.Seen > 10).Select(p => p.Key).ToArray()) policies.Remove(id);
            feature.status = "active";
        }
        catch (Exception ex) { Fail("runtime_failed", ex); }
    }
    private bool Helm(int prefabHash, out Vector3 position)
    {
        if (!helms.TryGetValue(prefabHash, out var value))
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            value = null;
            if (prefab && (prefab.name == "Raft" || prefab.name == "Karve" || prefab.name == "VikingShip" || prefab.name == "Drakkar"))
            {
                var ship = prefab.GetComponent<Ship>();
                if (ship && ship.m_shipControlls && ship.m_shipControlls.m_attachPoint)
                    value = prefab.transform.InverseTransformPoint(ship.m_shipControlls.m_attachPoint.position);
            }
            if (helms.Count < 64) helms[prefabHash] = value;
        }
        position = value ?? default; return value.HasValue;
    }
    private void Fail(string status, Exception ex) { feature.status = status; feature.detail = ex.GetBaseException().Message; policies.Clear(); log("Captain ownership vanilla fallback: " + feature.detail); }
    internal void Capture(ServerImprovementMetrics metrics)
    {
        metrics.captainEnabled = feature.enabled; metrics.captainStatus = feature.status; metrics.captainCandidates = candidates;
        metrics.captainTransfers = transfers; metrics.captainDeferred = deferred; transfers = deferred = 0;
    }
}
