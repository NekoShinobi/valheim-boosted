using System;
using System.Collections.Generic;

namespace ValheimBoosted;

// Limits apply to immutable queued payloads, including the interval while a backlog drains.
internal sealed class EarlyZdoQueue
{
    internal const int MaxPackets = 128, MaxBytes = 2 * 1024 * 1024, MaxPacketBytes = 256 * 1024;
    internal const double MaxAge = 15;
    private readonly Queue<Entry> items = new Queue<Entry>();
    private sealed class Entry { internal byte[] Bytes; internal double At; }
    internal int Bytes { get; private set; }
    internal int Count => items.Count;
    internal bool Expired(double now) => Count > 0 && now - items.Peek().At >= MaxAge;
    internal bool CanAdd(int bytes, double now) => bytes > 0 && bytes <= MaxPacketBytes && Count < MaxPackets
        && bytes <= MaxBytes - Bytes && !Expired(now);
    internal void Add(byte[] bytes, double now)
    {
        if (!CanAdd(bytes.Length, now)) throw new InvalidOperationException("Early ZDO queue limit exceeded");
        items.Enqueue(new Entry { Bytes = bytes, At = now }); Bytes += bytes.Length;
    }
    internal byte[] Take() { var entry = items.Dequeue(); Bytes -= entry.Bytes.Length; return entry.Bytes; }
    internal void Clear() { items.Clear(); Bytes = 0; }
}

internal sealed class CharacterFreshness
{
    private uint revision;
    private bool seen, changed;
    private double at;
    internal void Observe(uint value, double now)
    {
        if (seen && revision != value) { changed = true; at = now; }
        revision = value; seen = true;
    }
    internal bool Fresh(double now) => changed && now >= at && now - at <= 2;
}

internal static class ActorPriorityPolicy
{
    // Vanilla's age credit is 1.5 units/second. Cap our advantage at twelve seconds of that credit.
    internal static float Bonus(int category) => category == 3 ? 18 : category == 2 ? 12 : category == 1 ? 6 : 0;
    // Reserve every fourth pass for the original ordering, including old background changes.
    internal static bool Boost(long pass) => pass % 4 != 0;
}
