using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class ReplicationIntegration : IDisposable
{
    private readonly Harmony harmony = new Harmony(Plugin.PluginGuid + ".replication");
    private readonly TelemetryIntegration integration;
    private readonly Action<string> log;
    private readonly FairScheduler<object> policy = new FairScheduler<object>();
    private readonly Dictionary<ZRpc, PeerState> peers = new Dictionary<ZRpc, PeerState>();
    private readonly SampleWindow work = new SampleWindow();
    private readonly FeatureStatus scheduler, observer;
    private readonly int maxCalls, debtCap;
    private readonly double budgetMs;
    private ReplicationContracts contracts;
    private ZDOMan manager;
    private int calls, timeLimited, workLimited;
    private double discarded;
    private double nextAudit;
    internal static ReplicationIntegration Current;

    private sealed class PeerState
    {
        internal ReplicationMetrics Counts = new ReplicationMetrics();
        internal double? LastService, LastSend;
        internal readonly SampleWindow Duration = new SampleWindow(), Interval = new SampleWindow();
    }
    internal sealed class SendTrace
    {
        internal ZRpc Rpc;
        internal double Started;
        internal int Sent;
    }

    internal ReplicationIntegration(ConfigFile config, TelemetryIntegration integration, Action<string> log)
    {
        this.integration = integration; this.log = log;
        bool enabled = config.Bind("Scheduling", "Enabled", true, "Experimental dedicated Steam server scheduler. Restart required. Vanilla packet format and queue limits are preserved.").Value;
        maxCalls = config.Bind("Scheduling", "MaxCallsPerFrame", 4, new ConfigDescription("Maximum send opportunities per frame.", new AcceptableValueRange<int>(1, 32))).Value;
        budgetMs = config.Bind("Scheduling", "TimeBudgetMs", 2f, new ConfigDescription("Soft scheduling budget in milliseconds; an individual game send cannot be interrupted.", new AcceptableValueRange<float>(0.1f, 10f))).Value;
        debtCap = config.Bind("Scheduling", "MaxDebtPerPeer", 2, new ConfigDescription("Maximum accumulated send opportunities per peer after a hitch.", new AcceptableValueRange<int>(1, 4))).Value;
        bool observe = config.Bind("Features", "Replication", true, "Observe ZDO send opportunities, duration, outcomes and service age. Restart required.").Value;
        scheduler = new FeatureStatus { id = "FairScheduler", enabled = enabled, target = "ZDOMan.SendZDOToPeers2", status = !enabled ? "configured_disabled" : !integration.GameSupported ? "blocked_compatibility" : "available" };
        observer = new FeatureStatus { id = "Replication", enabled = observe, target = "ZDOMan.SendZDOs", status = !observe ? "configured_disabled" : !integration.GameSupported ? "blocked_compatibility" : "available" };
        integration.Features[scheduler.id] = scheduler; integration.Features[observer.id] = observer;
        Current = this;
        if (!scheduler.Collect && !observer.Collect) return;
        try
        {
            contracts = new ReplicationContracts();
            if (Foreign(contracts.Send) || Foreign(contracts.Schedule)) throw new InvalidOperationException("Another mod patches ZDO scheduling or sending");
            if (observer.Collect)
            {
                harmony.Patch(contracts.Send, prefix: Hook(nameof(BeforeSend)), finalizer: Hook(nameof(AfterSend)));
                observer.status = "installed_waiting";
            }
            if (scheduler.Collect)
            {
                harmony.Patch(contracts.Schedule, prefix: Hook(nameof(Schedule)));
                scheduler.status = "installed_waiting";
                scheduler.detail = "Awaiting a dedicated Steam server; experimental, 20 Hz target";
            }
            if (!Registered()) throw new InvalidOperationException("Replication patch registration mismatch");
        }
        catch (Exception ex) { Disable("installation_failed", ex); }
        log($"Replication: {observer.status}; scheduler: {scheduler.status}; max calls={maxCalls}; budget={budgetMs}ms; debt cap={debtCap}");
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(ReplicationIntegration), name);
    private bool Foreign(MethodInfo method) => Harmony.GetPatchInfo(method)?.Owners.Any(x => x != harmony.Id) == true;
    private bool Has(MethodInfo method, string name, bool finalizer)
    {
        var info = Harmony.GetPatchInfo(method);
        var patches = finalizer ? info?.Finalizers : info?.Prefixes;
        return patches != null && patches.Count(p => p.owner == harmony.Id && p.PatchMethod == AccessTools.Method(typeof(ReplicationIntegration), name)) == 1;
    }
    private int OwnCount(MethodInfo method)
    {
        var info = Harmony.GetPatchInfo(method);
        return info == null ? 0 : info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers).Count(p => p.owner == harmony.Id);
    }
    private bool Registered() => OwnCount(contracts.Schedule) == (scheduler.Collect ? 1 : 0)
        && OwnCount(contracts.Send) == (observer.Collect ? 2 : 0)
        && (!scheduler.Collect || Has(contracts.Schedule, nameof(Schedule), false))
        && (!observer.Collect || (Has(contracts.Send, nameof(BeforeSend), false) && Has(contracts.Send, nameof(AfterSend), true)));
    internal void Audit(double now)
    {
        if (contracts == null || (!scheduler.Collect && !observer.Collect) || now < nextAudit) return;
        nextAudit = now + 1;
        if (Foreign(contracts.Schedule) || Foreign(contracts.Send) || !Registered()) Disable("patch_changed", new InvalidOperationException("ZDO patch registration changed or another owner appeared"));
    }
    private void Disable(string status, Exception ex)
    {
        foreach (var feature in new[] { scheduler, observer }) if (feature.Collect) { feature.status = status; feature.detail = ex.GetBaseException().Message; }
        policy.Clear();
        harmony.UnpatchSelf();
        log("Replication fallback to vanilla: " + ex);
    }
    private void Reset(ZDOMan value)
    {
        if (ReferenceEquals(manager, value)) return;
        manager = value; policy.Clear(); peers.Clear(); work.Take();
        calls = timeLimited = workLimited = 0; discarded = 0;
    }
    internal void RefreshWorld(ZDOMan value) => Reset(value);
    private static bool Schedule(ZDOMan __instance, float __0) => Current?.Run(__instance, __0) ?? true;
    private bool Run(ZDOMan value, float dt)
    {
        if (!scheduler.Collect) return true;
        bool attempted = false;
        try
        {
            // Check competing owners at every replacement call, before changing game state.
            if (Foreign(contracts.Schedule) || Foreign(contracts.Send) || !Registered()) throw new InvalidOperationException("Conflicting or changed ZDO patch registration");
            Reset(value);
            var net = ZNet.instance;
            var eligible = new List<object>();
            bool supported = net && net.IsDedicated();
            foreach (object entry in (IList)contracts.Peers.GetValue(value))
            {
                var peer = (ZNetPeer)contracts.Peer.GetValue(entry);
                if (!peer.IsReady() || !peer.m_socket.IsConnected()) continue;
                supported &= peer.m_socket is ZSteamSocket;
                eligible.Add(entry);
            }
            if (!supported)
            {
                policy.Clear(); scheduler.status = "installed_waiting";
                scheduler.detail = "Vanilla scheduling: requires a dedicated server with Steam peers";
                return true;
            }
            // Maintain neutral vanilla state for a later fallback. Never replay a partial round.
            contracts.Timer.SetValue(value, 0f); contracts.Cursor.SetValue(value, -1);
            int actualCalls = 0;
            var result = policy.Run(eligible, dt, maxCalls, budgetMs, debtCap, () => TelemetryCollector.Now, peer =>
            {
                var connection = (ZNetPeer)contracts.Peer.GetValue(peer);
                if (!((IList)contracts.Peers.GetValue(value)).Contains(peer) || !connection.IsReady() || !connection.m_socket.IsConnected()) return;
                attempted = true; actualCalls++;
                contracts.Send.Invoke(value, new[] { peer, (object)false });
            });
            if (!scheduler.Collect) return false; // An observer may have failed during the send.
            scheduler.invocations++; scheduler.status = "active"; scheduler.detail = null;
            calls += actualCalls; timeLimited += result.timeLimited ? 1 : 0; workLimited += result.workLimited ? 1 : 0;
            discarded += result.discardedDebt; work.Add(result.elapsedMs);
            return false;
        }
        catch (Exception ex)
        {
            Disable("runtime_failed", ex);
            // A send may already have mutated game state: do not run vanilla again this frame.
            return !attempted;
        }
    }
    private static void BeforeSend(ZDOMan __instance, object __0, out SendTrace __state)
    {
        __state = null;
        var current = Current;
        if (current == null || !current.observer.Collect) return;
        try
        {
            current.Reset(__instance);
            var peer = (ZNetPeer)current.contracts.Peer.GetValue(__0);
            __state = new SendTrace { Rpc = peer.m_rpc, Started = TelemetryCollector.Now, Sent = (int)current.contracts.Sent.GetValue(__instance) };
        }
        catch (Exception ex) { current.Disable("probe_failed", ex); }
    }
    private static Exception AfterSend(ZDOMan __instance, bool __result, Exception __exception, SendTrace __state)
    {
        var current = Current;
        if (__state == null || current == null || !current.observer.Collect) return __exception;
        try
        {
            var state = current.State(__state.Rpc);
            double now = TelemetryCollector.Now;
            state.Counts.sendAttempts++;
            state.Counts.sentZdos += Math.Max(0, (int)current.contracts.Sent.GetValue(__instance) - __state.Sent);
            state.Duration.Add((now - __state.Started) * 1000);
            if (state.LastService.HasValue) state.Interval.Add((__state.Started - state.LastService.Value) * 1000);
            state.LastService = __state.Started;
            if (__exception != null) state.Counts.sendFailures++;
            else if (__result) { state.Counts.sentBatches++; state.LastSend = now; }
            else state.Counts.noDataOrDeferred++;
            current.observer.invocations++; current.observer.status = "active";
        }
        catch (Exception ex) { current.Disable("probe_failed", ex); }
        return __exception;
    }
    private PeerState State(ZRpc rpc)
    {
        if (!peers.TryGetValue(rpc, out var value)) peers[rpc] = value = new PeerState();
        return value;
    }
    internal void Received(ZRpc rpc, int bytes) { if (observer.Collect) State(rpc).Counts.receivedPayloadBytes = (State(rpc).Counts.receivedPayloadBytes ?? 0) + Math.Max(0, bytes); }
    internal ReplicationMetrics Take(ZRpc rpc, double now)
    {
        if (!observer.Collect) return null;
        var state = State(rpc); var result = state.Counts; state.Counts = new ReplicationMetrics();
        result.receivedPayloadBytes = integration.Features["ZdoReceive"].Collect ? (result.receivedPayloadBytes ?? 0) : (long?)null;
        result.serviceAgeSeconds = state.LastService.HasValue ? (double?)Math.Max(0, now - state.LastService.Value) : null;
        result.sendAgeSeconds = state.LastSend.HasValue ? (double?)Math.Max(0, now - state.LastSend.Value) : null;
        result.sendDurationMs = state.Duration.Take(); result.serviceIntervalMs = state.Interval.Take();
        return result;
    }
    internal SchedulerMetrics Snapshot(HashSet<ZRpc> live)
    {
        foreach (var key in peers.Keys.Where(x => !live.Contains(x)).ToArray()) peers.Remove(key);
        var value = new SchedulerMetrics { enabled = scheduler.enabled, status = scheduler.status, maxCallsPerFrame = maxCalls, budgetMs = budgetMs, debtCap = debtCap,
            eligiblePeers = policy.Count, pendingDebt = policy.Debt, calls = calls, timeLimitedFrames = timeLimited, workLimitedFrames = workLimited, discardedDebt = discarded, frameWorkMs = work.Take() };
        calls = timeLimited = workLimited = 0; discarded = 0;
        return value;
    }
    public void Dispose() { harmony.UnpatchSelf(); policy.Clear(); peers.Clear(); if (ReferenceEquals(Current, this)) Current = null; }
}
