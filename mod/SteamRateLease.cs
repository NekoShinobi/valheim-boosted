using System;

namespace ValheimBoosted;

internal sealed class SteamRateSetting { internal int Value; internal bool Inherited; }

// A reversible, connection-scoped lease. Callbacks keep native Steam calls out of policy tests.
internal sealed class SteamRateLease
{
    private readonly Func<bool, SteamRateSetting> read;
    private readonly Action<int?> write;
    private readonly PeerImprovementMetrics metrics;
    private SteamRateSetting original;
    private int? written;
    private bool finished;
    private int restoreAttempts;
    internal string Error { get; private set; }
    internal SteamRateLease(Func<bool, SteamRateSetting> read, Action<int?> write, PeerImprovementMetrics metrics)
    { this.read = read; this.write = write; this.metrics = metrics; }
    internal bool Tick(int requested)
    {
        if (finished) { if (written.HasValue && restoreAttempts < 3) Restore(); return false; }
        try
        {
            if (written.HasValue)
            {
                var current = read(true); metrics.effectiveMaxRateBytesPerSecond = current.Value;
                metrics.minimumRateBytesPerSecond = read(false).Value;
                if (current.Value != written || current.Inherited) { metrics.rateStatus = "external_override"; written = null; finished = true; }
                return false;
            }
            original = read(true);
            int minimum = read(false).Value;
            if (original.Value <= 0 || minimum < 0) throw new InvalidOperationException("Unsupported Steam rate value");
            metrics.originalMaxRateBytesPerSecond = original.Value; metrics.minimumRateBytesPerSecond = minimum;
            requested = Math.Max(requested, metrics.minimumRateBytesPerSecond.Value);
            if (original.Value >= requested)
            { metrics.rateStatus = "existing_limit_preserved"; metrics.effectiveMaxRateBytesPerSecond = original.Value; finished = true; return false; }
            written = requested; write(requested);
            metrics.effectiveMaxRateBytesPerSecond = read(true).Value;
            if (metrics.effectiveMaxRateBytesPerSecond != requested || read(false).Value != metrics.minimumRateBytesPerSecond)
                throw new InvalidOperationException("Steam rate readback mismatch");
            metrics.rateStatus = "applied"; return true;
        }
        catch (Exception ex)
        { Failed("write_or_readback_failed", ex); finished = true; Restore(); return false; }
    }
    internal void Restore()
    {
        finished = true;
        if (!written.HasValue || original == null) return;
        restoreAttempts++;
        try
        {
            var current = read(true);
            if (current.Value != written || current.Inherited)
            { metrics.rateStatus = "external_override"; written = null; return; }
            write(original.Inherited ? (int?)null : original.Value);
            var restored = read(true);
            if (original.Inherited ? !restored.Inherited : restored.Inherited || restored.Value != original.Value)
                throw new InvalidOperationException("Steam rate restoration readback mismatch");
            metrics.effectiveMaxRateBytesPerSecond = restored.Value; written = null; metrics.rateStatus = "restored";
        }
        catch (Exception ex) { Failed("restore_failed", ex); }
    }
    private void Failed(string status, Exception ex)
    { metrics.rateWriteFailures++; metrics.rateStatus = status; Error = ex.GetBaseException().Message; }
}
