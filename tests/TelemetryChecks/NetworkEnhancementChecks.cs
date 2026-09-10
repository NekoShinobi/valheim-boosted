using System;
using System.IO;
using ValheimBoosted;

internal static class NetworkEnhancementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var q = new EarlyZdoQueue();
        check(!q.CanAdd(0, 0) && !q.CanAdd(EarlyZdoQueue.MaxPacketBytes + 1, 0), "Reject empty/oversize early packets before copying");
        for (int i = 0; i < EarlyZdoQueue.MaxPackets; i++) q.Add(new[] { (byte)i }, 0);
        check(!q.CanAdd(1, 0), "Early queue packet bound");
        for (int i = 0; i < EarlyZdoQueue.MaxPackets; i++) check(q.Take()[0] == i, "Early queue preserves exact FIFO bytes");
        check(q.Bytes == 0 && q.Count == 0, "Drain releases queue memory");
        for (int i = 0; i < 8; i++) q.Add(new byte[EarlyZdoQueue.MaxPacketBytes], 1);
        check(!q.CanAdd(1, 1) && q.Bytes == EarlyZdoQueue.MaxBytes, "Early queue aggregate byte bound");
        check(!q.Expired(15.9) && q.Expired(16) && !q.CanAdd(1, 16), "Expiry applies during drain as well as before readiness");
        q.Clear(); check(q.Bytes == 0 && !q.Expired(100), "Disconnect clears all queued state");
        var fresh = new CharacterFreshness(); fresh.Observe(3, 0);
        check(!fresh.Fresh(0), "First-seen character revision cannot prove freshness");
        fresh.Observe(4, 1); check(fresh.Fresh(3) && !fresh.Fresh(3.001), "Character revision freshness expires");
        fresh.Observe(4, 4); check(!fresh.Fresh(4), "Reading an unchanged revision does not renew freshness");
        check(ActorPriorityPolicy.Bonus(3) == 18 && ActorPriorityPolicy.Bonus(2) == 12 && ActorPriorityPolicy.Bonus(1) == 6 && ActorPriorityPolicy.Bonus(0) == 0, "Actor advantages bounded to twelve seconds of vanilla age credit");
        check(ActorPriorityPolicy.Boost(1) && ActorPriorityPolicy.Boost(3) && !ActorPriorityPolicy.Boost(4) && !ActorPriorityPolicy.Boost(8), "Reserved vanilla pass in every four per peer");
        var key = new MapPlayerKey { User = 123, Id = 5 };
        var message = new MapMessage { Kind = 3, Updates = true, Forced = true, Sequence = 10, Points = new[] { new MapPoint { Key = key, X = 1, Y = 2, Z = 3 } } };
        var bytes = MapPositionProtocol.Encode(message); var decoded = MapPositionProtocol.Decode(bytes);
        check(bytes.Length == 38 && decoded.Forced && decoded.Sequence == 10 && decoded.Points[0].Key.Equals(key) && decoded.Points[0].Z == 3, "Map wire preserves identity, visibility and positions");
        foreach (int length in new[] { 0, 13, 37, MapPositionProtocol.MaxBytes + 1 })
        { bool rejected = false; try { MapPositionProtocol.Decode(new byte[length]); } catch (InvalidDataException) { rejected = true; } check(rejected, "Map size/length rejection"); }
        Action<MapMessage> invalid = m => { bool rejected = false; try { MapPositionProtocol.Encode(m); } catch (InvalidDataException) { rejected = true; } check(rejected, "Malformed map messages rejected"); };
        invalid(new MapMessage { Kind = 1, Forced = true });
        invalid(new MapMessage { Kind = 3, Updates = true, Sequence = 0 });
        invalid(new MapMessage { Kind = 3, Updates = true, Sequence = 1, Points = new[] { message.Points[0], message.Points[0] } });
        var p = message.Points[0]; p.X = float.NaN;
        invalid(new MapMessage { Kind = 3, Updates = true, Sequence = 1, Points = new[] { p } });
        var track = new MapPositionTrack(); p.X = 0; track.Add(p, 1);
        p.X = 10; track.Add(p, 2);
        check(track.Value(2.25).X == 5 && track.Value(3).X == 10 && track.Value(10).X == 10, "Marker interpolation is bounded and does not extrapolate");
        p.X = 1000; track.Add(p, 3); check(track.Value(3).X == 1000, "Teleport snaps rather than travelling across the map");
        p.X = 1001; track.Add(p, 10); check(track.Value(10).X == 1001, "Stale marker recovery snaps to a new sample");
    }
}
