namespace ValheimBoosted;

public sealed class ServerImprovementMetrics
{
    public bool windowsEnabled, rateEnabled, captainEnabled, compressionEnabled;
    public int targetBytesPerSecond, maximumWindowBytes, requestedMaxRateBytesPerSecond;
    public double compressionBudgetMs;
    public string captainStatus, steamConfigInterface;
    public int captainCandidates, captainTransfers, captainDeferred;
    public TimingSummary compressionEncodeMs, compressionDecodeMs;
    public long rawPayloadBytes, framedPayloadBytes, compressedSent, compressedReceived, compressionSkipped, compressionRejected;
}
public sealed class PeerImprovementMetrics
{
    public int windowBytes = SendWindowPolicy.VanillaBytes;
    public string windowStatus, rateStatus, compressionStatus;
    public int? originalMaxRateBytesPerSecond, effectiveMaxRateBytesPerSecond, minimumRateBytesPerSecond;
    public int rateWriteFailures;
}
