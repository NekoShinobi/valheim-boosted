using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimBoosted;

internal struct MapPlayerKey : IEquatable<MapPlayerKey>
{
    internal long User; internal uint Id;
    public bool Equals(MapPlayerKey other) => User == other.User && Id == other.Id;
    public override bool Equals(object other) => other is MapPlayerKey key && Equals(key);
    public override int GetHashCode() => User.GetHashCode() ^ Id.GetHashCode();
}
internal struct MapPoint { internal MapPlayerKey Key; internal float X, Y, Z; }
internal sealed class MapMessage
{
    internal byte Kind; // 1: client capability, 2: server acknowledgement/policy, 3: complete public positions
    internal bool Updates, Forced;
    internal long Sequence;
    internal MapPoint[] Points = Array.Empty<MapPoint>();
}
internal static class MapPositionProtocol
{
    internal const int MaxPlayers = 64, MaxBytes = 14 + 24 * MaxPlayers;
    internal static byte[] Encode(MapMessage message)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0xb04d); writer.Write((byte)1); writer.Write(message.Kind);
            writer.Write((byte)((message.Updates ? 1 : 0) | (message.Forced ? 2 : 0)));
            writer.Write(message.Sequence); writer.Write(checked((byte)message.Points.Length));
            foreach (var p in message.Points) { writer.Write(p.Key.User); writer.Write(p.Key.Id); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            var result = stream.ToArray(); Decode(result); return result;
        }
    }
    internal static MapMessage Decode(byte[] data)
    {
        if (data.Length < 14 || data.Length > MaxBytes) throw new InvalidDataException("Map message size");
        using (var stream = new MemoryStream(data, false))
        using (var reader = new BinaryReader(stream))
        {
            if (reader.ReadUInt16() != 0xb04d || reader.ReadByte() != 1) throw new InvalidDataException("Map protocol version");
            var message = new MapMessage { Kind = reader.ReadByte() }; byte flags = reader.ReadByte();
            message.Updates = (flags & 1) != 0; message.Forced = (flags & 2) != 0;
            message.Sequence = reader.ReadInt64(); int count = reader.ReadByte();
            if (message.Kind < 1 || message.Kind > 3 || flags > 3 || count > MaxPlayers || stream.Length != 14 + count * 24
                || (message.Kind != 3 && (count != 0 || message.Sequence != 0)) || (message.Kind == 3 && (message.Sequence <= 0 || !message.Updates))
                || (message.Kind == 1 && message.Forced)) throw new InvalidDataException("Map message fields");
            var keys = new HashSet<MapPlayerKey>(); message.Points = new MapPoint[count];
            for (int i = 0; i < count; i++)
            {
                var p = new MapPoint { Key = new MapPlayerKey { User = reader.ReadInt64(), Id = reader.ReadUInt32() }, X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                if (p.Key.User == 0 || p.Key.Id == 0 || !Finite(p.X) || !Finite(p.Y) || !Finite(p.Z) || !keys.Add(p.Key)) throw new InvalidDataException("Map position fields");
                message.Points[i] = p;
            }
            return message;
        }
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 1000000;
}

// Receipt-clock interpolation only. It changes marker rendering, never replicated object positions.
internal sealed class MapPositionTrack
{
    private MapPoint from, to;
    private double at;
    private bool initialized;
    internal MapPoint Value(double now)
    {
        float t = (float)Math.Max(0, Math.Min(1, (now - at) / .5));
        return new MapPoint { Key = to.Key, X = from.X + (to.X - from.X) * t, Y = from.Y + (to.Y - from.Y) * t, Z = from.Z + (to.Z - from.Z) * t };
    }
    internal void Add(MapPoint point, double now)
    {
        var displayed = Value(now);
        float dx = point.X - displayed.X, dy = point.Y - displayed.Y, dz = point.Z - displayed.Z;
        from = !initialized || now - at > 2 || dx * dx + dy * dy + dz * dz > 64 * 64 ? point : displayed;
        to = point; at = now; initialized = true;
    }
}
