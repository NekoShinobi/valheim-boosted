import type { Peer, Snapshot } from '../shared/telemetry';

export function snapshot(sequence = 1, now = Date.now()): Snapshot {
  return {
    schemaVersion: 1, modVersion: '0.3.0', processSession: 'test-process', worldSession: 'test-world',
    sequence, capturedAtUtc: new Date(now).toISOString(), sampleWindowSeconds: 1,
    role: 'dedicated_server', running: true, managedMemoryBytes: 128 * 1048576,
    frameIntervalMs: { samples: 60, percentileSamples: 60, mean: 16, p95: 18, max: 22 },
    networkUpdateDurationMs: { samples: 60, percentileSamples: 60, mean: 1, p95: 2, max: 3 },
    loadedObjects: 100, knownZdos: 200, ownershipStatus: 'available', loadedOwnership: [], peers: [],
  };
}
export function peer(): Peer {
  return {
    peerSessionId: '123456789', transport: 'ZSteamSocket', connected: true, measurementStatus: 'available',
    rttMs: 42, rttSampleDeltaMs: null, localDeliveryQuality: 1, remoteDeliveryQuality: 1,
    outgoingBytesPerSecond: 12000, incomingBytesPerSecond: 6000, outstandingBytes: 4096,
    applicationQueuedBytes: 0, pendingReliableBytes: 0, pendingUnreliableBytes: 0, sentUnacknowledgedReliableBytes: 4096,
    estimatedTransportQueueMs: 0, estimatedSendRateBytesPerSecond: 153600, lastZdoBatchReceivedAgoSeconds: null, zdoBatchesReceivedInWindow: 0,
  };
}
