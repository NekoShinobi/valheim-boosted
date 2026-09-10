using System;
using System.Collections.Generic;

namespace ValheimBoosted;

// Game-independent policy. A turn is a send opportunity, not guaranteed delivery.
internal sealed class FairScheduler<T>
{
    private sealed class Entry { internal T Peer; internal double Debt; }
    private readonly List<Entry> entries = new List<Entry>();
    private int cursor;
    internal int Count => entries.Count;
    internal double Debt { get { double n = 0; foreach (var e in entries) n += e.Debt; return n; } }
    internal void Clear() { entries.Clear(); cursor = 0; }

    internal SchedulerTurn Run(IReadOnlyList<T> peers, double elapsed, int maxCalls, double budgetMs,
        int debtCap, Func<double> clock, Action<T> service)
    {
        if (peers.Count == 0)
        {
            Clear();
            return new SchedulerTurn();
        }
        double started = clock();
        var live = new HashSet<T>(peers);
        for (int i = entries.Count - 1; i >= 0; i--)
            if (!live.Contains(entries[i].Peer)) { entries.RemoveAt(i); if (i < cursor) cursor--; }
        var known = new HashSet<T>();
        foreach (var entry in entries) known.Add(entry.Peer);
        foreach (var peer in peers) if (known.Add(peer)) entries.Add(new Entry { Peer = peer });
        if (entries.Count == 0) { cursor = 0; return new SchedulerTurn(); }
        cursor %= entries.Count;
        double added = double.IsNaN(elapsed) || double.IsInfinity(elapsed) ? 0 : Math.Max(0, elapsed) * 20;
        var result = new SchedulerTurn();
        foreach (var entry in entries)
        {
            double debt = entry.Debt + added;
            result.discardedDebt += Math.Max(0, debt - debtCap);
            entry.Debt = Math.Min(debtCap, debt);
        }
        // At most one visit per peer per frame, even after a hitch.
        for (int visited = 0; visited < entries.Count; visited++)
        {
            if (result.calls >= maxCalls) { result.workLimited = true; break; }
            if ((clock() - started) * 1000 >= budgetMs) { result.timeLimited = true; break; }
            var entry = entries[cursor];
            cursor = (cursor + 1) % entries.Count;
            if (entry.Debt + 1e-9 < 1) continue;
            entry.Debt = Math.Max(0, entry.Debt - 1);
            result.calls++;
            service(entry.Peer);
        }
        result.elapsedMs = Math.Max(0, (clock() - started) * 1000);
        result.timeLimited |= result.elapsedMs >= budgetMs;
        return result;
    }
}

internal sealed class SchedulerTurn
{
    internal int calls;
    internal bool workLimited, timeLimited;
    internal double discardedDebt, elapsedMs;
}
