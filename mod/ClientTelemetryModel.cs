using System;

namespace ValheimBoosted;

public sealed class ClientWindow
{
    public long sequence;
    public double startMs, endMs;
    public int frames;
    public double? frameMeanMs, frameP95Ms, frameP99Ms, frameMaxMs, worstFrameEndMs;
    public int? longFrames50, longFrames100, longFrames250;
    public int gc0, gc1, gc2;
    public double? cpuPercent, managedMemoryMiB, networkP95Ms, rttMs, queueMs;
    public int? changedZdos;
    public bool focused, loading;
    public int frameLimit, vSyncCount;
    public double? markerMs;
}

public sealed class ClientTelemetryPoint
{
    public string streamId, peerSessionId, playerId;
    public double startMs, endMs, receivedAtMs, clockErrorMs;
    public ClientWindow sample;
}

public sealed class ClientTelemetryPeer
{
    public string streamId, peerSessionId, playerId, name, modBuildId, status;
    public double sessionStartedAtMs, lastSeenAtMs;
    public double? sessionEndedAtMs;
    public double? clockErrorMs, lastReceivedAtMs;
}

public sealed class ClientTelemetrySnapshot
{
    public int protocolVersion = 1;
    public bool receiveEnabled, shareEnabled;
    public string status;
    public string identityStatus;
    public long sentBytes, receivedBytes, rejectedMessages, congestionSkips, droppedSamples;
    public ClientTelemetryPeer[] peers;
    public ClientTelemetryPeer[] sessions;
    public ClientTelemetryPoint[] samples;
}

// Four timestamps, all in milliseconds. Only the server computes the offset.
internal sealed class ClientClock
{
    internal double Offset { get; private set; }
    internal double Error { get; private set; }
    internal double UpdatedAt { get; private set; }
    internal bool Ready { get; private set; }
    internal bool Update(double s1, double c2, double c3, double s4)
    {
        if (!Finite(s1) || !Finite(c2) || !Finite(c3) || !Finite(s4)
            || c2 < 0 || c3 > 9007199254740991d || c3 < c2 || s4 < s1 || s4 - s1 > 10000) return false;
        double delay = (s4 - s1) - (c3 - c2);
        if (delay < 0 || delay > 10000) return false;
        // Prefer a less congested exchange; refresh old estimates to track drift.
        if (!Ready || delay / 2 <= Uncertainty(s4) || s4 - UpdatedAt > 60000)
        {
            Offset = ((s1 - c2) + (s4 - c3)) / 2;
            Error = delay / 2; UpdatedAt = s4; Ready = true;
        }
        return true;
    }
    internal double Uncertainty(double now) => Error + Math.Max(0, now - UpdatedAt) * 0.0002;
    internal bool Current(double now) => Ready && now - UpdatedAt <= 120000;
    internal static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}
