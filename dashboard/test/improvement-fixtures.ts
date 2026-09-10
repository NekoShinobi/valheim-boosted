import { historySnapshot } from './history-fixtures';
import type { ServerImprovements, PeerImprovements } from '../shared/improvements';
export const improvements = (): ServerImprovements => ({
  windowsEnabled: true, rateEnabled: true, captainEnabled: true, compressionEnabled: true,
  targetBytesPerSecond: 153600, maximumWindowBytes: 32768, requestedMaxRateBytesPerSecond: 307200, compressionBudgetMs: 1,
  steamConfigInterface: 'Steamworks.SteamGameServerNetworkingUtils', captainStatus: 'active', captainCandidates: 1, captainTransfers: 1, captainDeferred: 0,
  compressionEncodeMs: { samples: 2, percentileSamples: 2, mean: 0.2, p95: 0.3, max: 0.3 },
  compressionDecodeMs: { samples: 1, percentileSamples: 1, mean: 0.1, p95: 0.1, max: 0.1 },
  rawPayloadBytes: 10240, framedPayloadBytes: 2048, compressedSent: 2, compressedReceived: 1, compressionSkipped: 3, compressionRejected: 0,
});
export const peerImprovements = (): PeerImprovements => ({ windowBytes: 32768, windowStatus: 'rtt_allowance', rateStatus: 'applied', compressionStatus: 'negotiated',
  originalMaxRateBytesPerSecond: 153600, effectiveMaxRateBytesPerSecond: 307200, minimumRateBytesPerSecond: 153600, rateWriteFailures: 0 });
export function improvementSnapshot(sequence = 1, now = Date.now()) {
  const s = historySnapshot(sequence, now); s.serverImprovements = improvements(); s.peers[0].improvements = peerImprovements();
  s.features = ['SendWindows', 'SteamRate', 'CaptainOwnership', 'Compression'].map(id => ({ id, enabled: true, status: 'active', detail: null, target: null, invocations: 2 }));
  return s;
}
