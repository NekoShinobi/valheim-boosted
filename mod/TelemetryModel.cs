using System;

namespace ValheimBoosted;

// Plain managed snapshots: never pass Unity objects to the export thread.
public sealed class TelemetrySnapshot
{
    public int schemaVersion = 1;
    public string modVersion = Plugin.PluginVersion;
    public string processSession;
    public string worldSession;
    public long sequence;
    public string capturedAtUtc;
    public double uptimeSeconds;
    public double sampleWindowSeconds;
    public string role;
    public bool running = true;
    public TimingSummary frameIntervalMs;
    public TimingSummary networkUpdateDurationMs;
    public int fixedUpdates;
    public double fixedStepSeconds;
    public long managedMemoryBytes;
    public int[] gcCollections;
    public int readyPeers;
    public int? knownZdos;
    public int? zdosSentLastGameSecond;
    public int? zdosReceivedLastGameSecond;
    public int? clientChangedZdos;
    public int? loadedObjects;
    public OwnerCount[] loadedOwnership;
    public string ownershipStatus;
    public string networkTimingStatus;
    public string zdoReceiveStatus;
    public string exportError;
    public PeerMetrics[] peers;
}

public sealed class TimingSummary
{
    public int samples;
    public int percentileSamples;
    public double? mean;
    public double? p95;
    public double? max;
}

public sealed class OwnerCount
{
    // Opaque session IDs, never Steam IDs or network addresses.
    public string ownerSessionId;
    public int objects;
}

public sealed class PeerMetrics
{
    public string peerSessionId;
    public string transport;
    public bool connected;
    public string measurementStatus;
    public double? rttMs;
    public double? rttSampleDeltaMs;
    public double? localDeliveryQuality;
    public double? remoteDeliveryQuality;
    public double? outgoingBytesPerSecond;
    public double? incomingBytesPerSecond;
    public long? outstandingBytes;
    public long? applicationQueuedBytes;
    public int? pendingReliableBytes;
    public int? pendingUnreliableBytes;
    public int? sentUnacknowledgedReliableBytes;
    public double? estimatedTransportQueueMs;
    public int? estimatedSendRateBytesPerSecond;
    public double? lastZdoBatchReceivedAgoSeconds;
    public int zdoBatchesReceivedInWindow;
}

internal sealed class SampleWindow
{
    private readonly double[] values = new double[2048];
    private int count;
    private double sum;
    private double maximum;

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return;
        values[count % values.Length] = value;
        count++;
        sum += value;
        maximum = Math.Max(maximum, value);
    }

    public TimingSummary Take()
    {
        int n = Math.Min(count, values.Length);
        var sorted = new double[n];
        Array.Copy(values, sorted, n);
        Array.Sort(sorted);
        var result = new TimingSummary { samples = count, percentileSamples = n };
        if (count > 0)
        {
            result.mean = sum / count;
            result.max = maximum;
            result.p95 = sorted[Math.Max(0, (int)Math.Ceiling(n * 0.95) - 1)];
        }
        count = 0;
        sum = maximum = 0;
        return result;
    }
}
