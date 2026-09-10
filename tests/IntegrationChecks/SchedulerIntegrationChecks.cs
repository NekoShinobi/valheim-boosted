using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimBoosted;

namespace ValheimBoosted
{
    // Fixtures replace only game contracts/clock; actual scheduler integration and Harmony execute.
    internal static class TelemetryCollector { private static double now; internal static double ClockStep; internal static double Now { get { double v = now; now += ClockStep; return v; } set => now = value; } }
    internal sealed class ReplicationContracts
    {
        internal readonly MethodInfo Schedule = typeof(ZDOMan).GetMethod("SendZDOToPeers2"), Send = typeof(ZDOMan).GetMethod("SendZDOs");
        internal readonly FieldInfo Peers = typeof(ZDOMan).GetField("Peers"), Peer = typeof(ZDOMan.ZDOPeer).GetField("m_peer"), Sent = typeof(ZDOMan).GetField("Sent"), Timer = typeof(ZDOMan).GetField("Timer"), Cursor = typeof(ZDOMan).GetField("Cursor");
    }
}
internal static class SchedulerIntegrationChecks
{
    static void Overlap() { }
    internal static void Run(Action<bool, string> check)
    {
        ZNet.instance = new ZNet();
        var config = new BepInEx.Configuration.ConfigFile();
        // This scheduler fixture has no real serializer IL. Send transforms are
        // checked against actual game assemblies in GameContractChecks.
        foreach (var section in new[] { "SendWindows", "SteamRate", "Compression" })
            config.Switches[section + ".Enabled"] = false;
        using (var telemetry = new TelemetryIntegration(config, _ => { }))
        {
            var game = new ZDOMan(); var peer = new ZNetPeer(); game.Peers.Add(new ZDOMan.ZDOPeer { m_peer = peer });
            using (var replication = new ReplicationIntegration(config, telemetry, _ => { }))
            {
                game.SendZDOToPeers2(.05f);
                check(game.Sends == 1 && game.VanillaCalls == 0, "Default-on scheduler replaces only the normal send round");
                var sample = replication.Take(peer.m_rpc, 1);
                check(sample.sendAttempts == 1 && sample.sentBatches == 1 && sample.sentZdos == 1, "Send hook records actual vanilla serialization outcome");
                peer.m_socket.Connected = false;
                game.SendZDOToPeers2(.05f);
                check(game.Sends == 1, "Disconnected peer is ineligible");
                peer.m_socket.Connected = true; ZNet.instance.Dedicated = false;
                game.SendZDOToPeers2(.05f);
                check(game.VanillaCalls == 1, "Client/host retains vanilla scheduling");
                ZNet.instance.Dedicated = true;
                peer.m_socket = new TestSocket(); game.SendZDOToPeers2(.05f);
                check(game.VanillaCalls == 2, "Mixed or non-Steam transport retains vanilla scheduling");
                peer.m_socket = new ZSteamSocket();
                var foreign = new Harmony("scheduler.other");
                try
                {
                    foreign.Patch(typeof(ZDOMan).GetMethod("SendZDOs"), prefix: new HarmonyMethod(typeof(SchedulerIntegrationChecks).GetMethod("Overlap", BindingFlags.NonPublic | BindingFlags.Static)));
                    game.SendZDOToPeers2(.05f);
                    check(game.VanillaCalls == 3 && !telemetry.Features["FairScheduler"].Collect, "Late foreign owner triggers immediate vanilla fallback");
                    check(Harmony.GetPatchInfo(typeof(ZDOMan).GetMethod("SendZDOs")).Owners.Contains("scheduler.other"), "Fallback preserves another mod's hook");
                }
                finally { foreign.UnpatchSelf(); }
            }
            check(Harmony.GetPatchInfo(typeof(ZDOMan).GetMethod("SendZDOToPeers2"))?.Owners.Contains(Plugin.PluginGuid + ".replication") != true, "Scheduler hooks cleaned up");
        }
        using (var telemetry = new TelemetryIntegration(config, _ => { }))
        using (var replication = new ReplicationIntegration(config, telemetry, _ => { }))
        {
            var game = new ZDOMan { ThrowSend = true }; game.Peers.Add(new ZDOMan.ZDOPeer { m_peer = new ZNetPeer() });
            game.SendZDOToPeers2(.05f);
            check(game.Sends == 1 && game.VanillaCalls == 0, "Failed partial round is not replayed through vanilla in the same frame");
            game.SendZDOToPeers2(.05f);
            check(game.VanillaCalls == 1, "Subsequent frame falls back to vanilla after failure");
        }
        using (var telemetry = new TelemetryIntegration(config, _ => { }))
        using (var replication = new ReplicationIntegration(config, telemetry, _ => { }))
        {
            var game = new ZDOMan();
            game.Peers.Add(new ZDOMan.ZDOPeer { m_peer = new ZNetPeer() });
            var leaving = new ZDOMan.ZDOPeer { m_peer = new ZNetPeer() }; game.Peers.Add(leaving);
            game.AfterSend = () => game.Peers.Remove(leaving);
            game.SendZDOToPeers2(.05f);
            check(game.Sends == 1, "Peer removed during a round is rechecked before sending");
            replication.RefreshWorld(null);
            check(replication.Snapshot(new HashSet<ZRpc>()).eligiblePeers == 0, "World unload clears service debt and peer state");
        }
        using (var telemetry = new TelemetryIntegration(config, _ => { }))
        using (var replication = new ReplicationIntegration(config, telemetry, _ => { }))
        {
            var game = new ZDOMan(); game.Peers.Add(new ZDOMan.ZDOPeer { m_peer = new ZNetPeer { m_rpc = null } });
            game.SendZDOToPeers2(.05f);
            check(!telemetry.Features["FairScheduler"].Collect && game.Sends == 1 && game.VanillaCalls == 0, "Observer failure during a send stays disabled and does not replay work");
        }
        config.Scheduling = false;
        using (var telemetry = new TelemetryIntegration(config, _ => { }))
        using (var replication = new ReplicationIntegration(config, telemetry, _ => { }))
        {
            var game = new ZDOMan(); game.SendZDOToPeers2(.05f);
            check(game.VanillaCalls == 1 && telemetry.Features["FairScheduler"].status == "configured_disabled", "Explicitly disabled scheduler keeps vanilla behavior with observers installed");
        }
    }
}
