using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ValheimBoosted;

namespace UnityEngine { public struct Vector3 {
    public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public static Vector3 MoveTowards(Vector3 a, Vector3 b, float max) => new Vector3(a.x + Math.Min(max, b.x - a.x), a.y, a.z);
} }
namespace ValheimBoosted {
    internal sealed class ReplicationRelevance {
        internal UnityEngine.Vector3 Position(ZNetPeer p, UnityEngine.Vector3 fallback) => fallback;
        internal static bool Finite(UnityEngine.Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x);
    }
}
internal static class MapIntegrationChecks
{
    private const string Rpc = "valheim.boosted.map.v1";
    private static ConfigFile Config()
    {
        var config = new ConfigFile(); config.Switches["Map.FastUpdates"] = false; config.Switches["Map.ForceLocationSharing"] = false; return config;
    }
    private static void Activate(TelemetryIntegration integration)
    { foreach (var id in new[] { "ForceMapSharing", "MapUpdates" }) { integration.Features[id].status = "available"; integration.Features[id].enabled = true; } }
    private static byte[] Last(ZNetPeer peer) => ((ZPackage)peer.m_rpc.Sent.Last(s => s.Item1 == Rpc).Item2[0]).GetArray();
    internal static void Run(Action<bool, string> check)
    {
        var server = new ZNet(); var client = new ZNet { Dedicated = false, Server = false };
        var modded = new ZNetPeer { m_characterID = new ZDOID { UserID = 1, ID = 1 }, m_refPos = new UnityEngine.Vector3(10, 0, 0) };
        var vanilla = new ZNetPeer { m_characterID = new ZDOID { UserID = 2, ID = 1 }, m_refPos = new UnityEngine.Vector3(30, 0, 0) };
        var remote = new ZNetPeer { m_server = true }; server.Peers.AddRange(new[] { modded, vanilla }); client.Peers.Add(remote);
        TelemetryCollector.Now = 1;
        using (var st = new TelemetryIntegration(Config(), _ => { }))
        using (var ct = new TelemetryIntegration(Config(), _ => { }))
        using (var s = new MapSharingIntegration(Config(), st, new ReplicationRelevance(), _ => { }))
        using (var c = new MapSharingIntegration(Config(), ct, new ReplicationRelevance(), _ => { }))
        {
            // Fixture game bodies are unreviewed: constructors stay disabled; exercise transport independently of patches.
            Activate(st); Activate(ct);
            ZNet.instance = server; s.Tick(1);
            check(modded.m_rpc.Sent.Count == 0 && vanilla.m_rpc.Sent.Count == 0, "Server sends no custom map messages before client capability");
            ZNet.instance = client; c.Tick(1); modded.m_rpc.Deliver(Rpc, Last(remote));
            ZNet.instance = server; TelemetryCollector.Now = 2; s.Tick(2);
            check(vanilla.m_rpc.Sent.Count == 0 && s.CapablePeers == 1, "Unmodded clients never receive custom map packets");
            var messages = modded.m_rpc.Sent.Where(p => p.Item1 == Rpc).Select(p => ((ZPackage)p.Item2[0]).GetArray()).ToArray();
            check(messages.Length == 2 && MapPositionProtocol.Decode(messages[0]).Kind == 2 && MapPositionProtocol.Decode(messages[1]).Kind == 3, "Capability acknowledgement precedes position payload");
            foreach (var message in messages) remote.m_rpc.Deliver(Rpc, message);
            check(c.ServerRequiresSharing && MapPositionProtocol.Decode(messages[1]).Points.Length == 2, "Default policy includes players who have voluntary sharing off");
            var players = new List<ZNet.PlayerInfo> { new ZNet.PlayerInfo { m_characterID = vanilla.m_characterID, m_publicPosition = true } };
            AccessTools.Method(typeof(MapSharingIntegration), "PublicPlayers").Invoke(null, new object[] { client, players });
            check(players[0].m_position.x == 30, "Negotiated display uses public player identity and current position");
            var marker = AccessTools.Method(typeof(MapSharingIntegration), "MarkerPosition");
            var markerPosition = (UnityEngine.Vector3)marker.Invoke(null, new object[] { new UnityEngine.Vector3(0, 0, 0), new UnityEngine.Vector3(5000, 0, 0), 5f });
            check(markerPosition.x == 5000, "Negotiated interpolation bypasses native MoveTowards so teleports snap immediately");
            int before = modded.m_rpc.Sent.Count; modded.m_socket.QueueBytes = 10241; s.Tick(3);
            check(modded.m_rpc.Sent.Count == before && s.Skipped > 0, "Optional map packets yield to game send queue"); modded.m_socket.QueueBytes = 0;
            st.Features["ForceMapSharing"].status = "configured_disabled"; TelemetryCollector.Now = 4; s.Tick(4);
            var privateUpdate = Last(modded); check(MapPositionProtocol.Decode(privateUpdate).Points.Length == 0, "Voluntary mode omits private positions entirely");
            remote.m_rpc.Deliver(Rpc, privateUpdate); players.Add(new ZNet.PlayerInfo { m_characterID = modded.m_characterID });
            AccessTools.Method(typeof(MapSharingIntegration), "PublicPlayers").Invoke(null, new object[] { client, players });
            check(players.Count == 0 && !c.ServerRequiresSharing, "Complete visibility update immediately removes private markers");
            long rejected = c.Rejected; remote.m_rpc.Deliver(Rpc, privateUpdate);
            check(c.Rejected == rejected + 1, "Duplicate map sequence rejected");
            vanilla.m_publicRefPos = true; TelemetryCollector.Now = 5; s.Tick(5); remote.m_rpc.Deliver(Rpc, Last(modded));
            players.Add(new ZNet.PlayerInfo { m_characterID = vanilla.m_characterID, m_position = new UnityEngine.Vector3(99, 0, 0) });
            TelemetryCollector.Now = 8; AccessTools.Method(typeof(MapSharingIntegration), "PublicPlayers").Invoke(null, new object[] { client, players });
            check(players[0].m_position.x == 99, "Stale map stream falls back to vanilla positions");
            markerPosition = (UnityEngine.Vector3)marker.Invoke(null, new object[] { new UnityEngine.Vector3(0, 0, 0), new UnityEngine.Vector3(5000, 0, 0), 5f });
            check(markerPosition.x == 5, "Stale stream restores vanilla marker motion");
            for (int i = 0; i < 5; i++) remote.m_rpc.Deliver(Rpc, new byte[MapPositionProtocol.MaxBytes + 1]);
            check(c.Rejected == 5, "Oversized/malformed stream is disabled after five rejections; later messages do no work");
            ZNet.instance = client; remote.m_socket.Close(); c.Tick(9);
            check(remote.m_rpc.Handlers.Count == 0 && !c.ServerRequiresSharing, "Disconnect clears map handler and server policy");
        }
        check(modded.m_rpc.Handlers.Count == 0 && vanilla.m_rpc.Handlers.Count == 0, "Map disposal releases all registrations");
        ZNet.instance = null;
    }
}
