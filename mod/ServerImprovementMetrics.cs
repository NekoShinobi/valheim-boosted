namespace ValheimBoosted;

public sealed class ServerImprovementMetrics
{
    public NetworkEnhancementMetrics network;
    public bool windowsEnabled, rateEnabled, captainEnabled, compressionEnabled;
    public int targetBytesPerSecond, maximumWindowBytes, requestedMaxRateBytesPerSecond;
    public double compressionBudgetMs;
    public string captainStatus, steamConfigInterface;
    public int captainCandidates, captainTransfers, captainDeferred;
    public TimingSummary compressionEncodeMs, compressionDecodeMs;
    public long rawPayloadBytes, framedPayloadBytes, compressedSent, compressedReceived, compressionSkipped, compressionRejected;
}
public sealed class NetworkEnhancementMetrics
{
    // Counts/bytes cover the snapshot window. Gauges are the current queue/peer/policy state.
    public long freshPositions, positionFallbacks, actorBonuses, vanillaPriorityPasses;
    public long earlyBuffered, earlyReplayed, earlyFailures;
    public int earlyQueuedBytes, mapCapablePeers;
    public bool forcedSharing;
    public long mapSentBytes, mapReceivedBytes, mapPackets, mapSkipped, mapRejected;
}
public sealed class PeerImprovementMetrics
{
    public int windowBytes = SendWindowPolicy.VanillaBytes;
    public string windowStatus, rateStatus, compressionStatus;
    public int? originalMaxRateBytesPerSecond, effectiveMaxRateBytesPerSecond, minimumRateBytesPerSecond;
    public int rateWriteFailures;
}
