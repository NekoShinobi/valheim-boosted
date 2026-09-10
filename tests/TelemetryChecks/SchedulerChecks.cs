using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBoosted;

internal static class SchedulerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        double clock = 0;
        var policy = new FairScheduler<int>();
        var peers = Enumerable.Range(0, 10).ToArray();
        var counts = new int[10];
        var last = new int[10];
        for (int frame = 1; frame <= 300; frame++)
        {
            int current = frame;
            var result = policy.Run(peers, 1.0 / 30, 4, 2, 2, () => clock, id => { counts[id]++; last[id] = current; clock += .0001; });
            check(result.calls <= 4 && last.All(x => frame - x <= 4), "Fair service under saturated ten-peer load without starvation");
        }
        check(counts.Max() - counts.Min() <= 1, "Fair cursor distributes bounded opportunities evenly");
        policy.Clear();
        var hitch = policy.Run(peers, 60, 32, 100, 2, () => clock, _ => { });
        int after = policy.Run(peers, 0, 32, 100, 2, () => clock, _ => { }).calls;
        int exhausted = policy.Run(peers, 0, 32, 100, 2, () => clock, _ => { }).calls;
        check(hitch.calls == 10 && after == 10 && exhausted == 0 && hitch.discardedDebt > 100, "Hitch debt is capped; at most one visit per peer per frame");
        policy.Clear(); var order = new List<int>();
        var expensive = policy.Run(new[] { 1, 2 }, .1, 4, 2, 2, () => clock, id => { order.Add(id); clock += .01; });
        policy.Run(new[] { 1, 2 }, .1, 4, 2, 2, () => clock, id => { order.Add(id); clock += .01; });
        check(expensive.calls == 1 && expensive.timeLimited && order.SequenceEqual(new[] { 1, 2 }), "Slow indivisible send ends the frame and preserves fairness next frame");
        policy.Run(new[] { 2, 3, 3 }, .05, 4, 100, 2, () => clock, id => check(id != 1, "Disconnected peer is never scheduled"));
        check(policy.Count == 2, "Reconnect/churn drops stale records and deduplicates eligibility");
        policy.Run(Array.Empty<int>(), .1, 4, 2, 2, () => clock, _ => { });
        check(policy.Count == 0 && policy.Debt == 0, "Empty world clears scheduler state");
        policy.Run(new[] { 4 }, double.NaN, 4, 2, 2, () => clock, _ => throw new Exception("Unexpected service"));
        check(policy.Debt == 0, "Invalid elapsed time cannot create service credit");
        var resource = new ResourceSampler().Capture(1);
        check(resource.status == "available" && resource.cpuPercentOneCore == null && resource.residentBytes > 0, "Resource sampler reports RSS and waits for CPU baseline");
    }
}
