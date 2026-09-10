using System;

namespace ValheimBoosted;

// Bytes in flight and bytes waiting for transmission are different signals.
internal sealed class SendWindowPolicy
{
    internal const int VanillaBytes = 10240;
    internal int Bytes { get; private set; } = VanillaBytes;
    internal string Reason { get; private set; } = "warming_up";
    private int healthy, congested;

    internal int Observe(bool available, double? rttMs, double? queueMs, long? managedBytes,
        int? pendingBytes, int? sendRate, int targetBytesPerSecond, int maximumBytes)
    {
        if (!available || !Finite(rttMs) || !Finite(queueMs) || !managedBytes.HasValue || managedBytes < 0
            || !pendingBytes.HasValue || pendingBytes < 0 || !sendRate.HasValue || sendRate <= 0)
        { healthy = congested = 0; Bytes = VanillaBytes; Reason = "metrics_unavailable"; return Bytes; }
        maximumBytes = Math.Max(VanillaBytes, Math.Min(65536, maximumBytes));
        // Sustained unsent backlog shrinks the allowance; unacknowledged bytes do not trigger this.
        bool busy = queueMs > 100 || managedBytes > 0 || pendingBytes > Math.Max(VanillaBytes, sendRate.Value / 10);
        if (busy)
        {
            healthy = 0; congested++;
            if (congested >= 2) Bytes = Math.Max(VanillaBytes, Bytes / 2);
            Reason = "queue_pressure"; return Bytes;
        }
        congested = 0;
        if (++healthy < 3) { Reason = "warming_up"; return Bytes; }
        // Configured demand is a ceiling; Steam's current estimate limits the candidate.
        double rate = Math.Min(Math.Max(1, targetBytesPerSecond), sendRate.Value);
        int target = (int)Math.Min(maximumBytes, Math.Max(VanillaBytes, rate * Math.Min(rttMs.Value, 1000) / 1000 + 4096));
        Bytes = target < Bytes ? target : Math.Min(target, Bytes + 2048);
        Reason = Bytes > VanillaBytes ? "rtt_allowance" : "vanilla_sufficient";
        return Bytes;
    }
    private static bool Finite(double? value) => value.HasValue && value >= 0 && !double.IsInfinity(value.Value) && !double.IsNaN(value.Value);
}
