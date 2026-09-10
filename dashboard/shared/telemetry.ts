export interface Peer {
  peerSessionId: string;
  transport: string;
  connected: boolean;
  measurementStatus: string;
  rttMs: number | null;
  rttSampleDeltaMs: number | null;
  localDeliveryQuality: number | null;
  remoteDeliveryQuality: number | null;
  outgoingBytesPerSecond: number | null;
  incomingBytesPerSecond: number | null;
  outstandingBytes: number | null;
  applicationQueuedBytes: number | null;
  pendingReliableBytes: number | null;
  pendingUnreliableBytes: number | null;
  sentUnacknowledgedReliableBytes: number | null;
  estimatedTransportQueueMs: number | null;
  estimatedSendRateBytesPerSecond: number | null;
  lastZdoBatchReceivedAgoSeconds: number | null;
  zdoBatchesReceivedInWindow: number;
}

export interface Timing {
  samples: number;
  percentileSamples: number;
  mean: number | null;
  p95: number | null;
  max: number | null;
}

export interface Snapshot {
  schemaVersion: 1;
  modVersion: string;
  processSession: string;
  worldSession: string | null;
  sequence: number;
  capturedAtUtc: string;
  sampleWindowSeconds: number;
  role: string;
  running: boolean;
  frameIntervalMs: Timing | null;
  networkUpdateDurationMs: Timing | null;
  managedMemoryBytes: number;
  loadedObjects: number | null;
  knownZdos: number | null;
  ownershipStatus: string;
  loadedOwnership: { ownerSessionId: string; objects: number }[] | null;
  peers: Peer[];
}

export type Status = 'waiting' | 'live' | 'stale' | 'stopped' | 'invalid';
export interface HistoryPoint {
  at: number;
  frameP95: number | null;
  networkP95: number | null;
  peers: Pick<Peer, 'peerSessionId' | 'rttMs' | 'estimatedTransportQueueMs' | 'outgoingBytesPerSecond' | 'incomingBytesPerSecond'>[];
}
export interface MetricsResponse {
  status: Status;
  message: string;
  lastReadAtUtc: string | null;
  snapshot: Snapshot | null;
  history: HistoryPoint[];
}

const object = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x);
const number = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x) && x >= 0;
const nullableNumber = (x: unknown) => x === null || number(x);
const timing = (x: unknown) => x === null || (object(x) && number(x.samples) && number(x.percentileSamples) && ['mean', 'p95', 'max'].every(k => nullableNumber(x[k])));
const peerNumbers = ['rttMs', 'rttSampleDeltaMs', 'localDeliveryQuality', 'remoteDeliveryQuality', 'outgoingBytesPerSecond', 'incomingBytesPerSecond', 'outstandingBytes', 'applicationQueuedBytes', 'pendingReliableBytes', 'pendingUnreliableBytes', 'sentUnacknowledgedReliableBytes', 'estimatedTransportQueueMs', 'estimatedSendRateBytesPerSecond', 'lastZdoBatchReceivedAgoSeconds'];

export function parseSnapshot(value: unknown): Snapshot {
  if (!object(value) || value.schemaVersion !== 1) throw new Error('Unsupported telemetry schema');
  if (typeof value.processSession !== 'string' || !(typeof value.worldSession === 'string' || value.worldSession === null)
    || !Number.isSafeInteger(value.sequence) || !number(value.sequence)
    || typeof value.capturedAtUtc !== 'string' || !Number.isFinite(Date.parse(value.capturedAtUtc))
    || typeof value.running !== 'boolean' || typeof value.role !== 'string' || typeof value.modVersion !== 'string'
    || !number(value.sampleWindowSeconds) || !number(value.managedMemoryBytes)
    || !nullableNumber(value.loadedObjects) || !nullableNumber(value.knownZdos)
    || !timing(value.frameIntervalMs) || !timing(value.networkUpdateDurationMs)
    || !Array.isArray(value.peers) || value.peers.length > 256) throw new Error('Invalid telemetry snapshot');
  const ids = new Set<string>();
  for (const peer of value.peers) {
    if (!object(peer) || typeof peer.peerSessionId !== 'string' || ids.has(peer.peerSessionId)
      || typeof peer.transport !== 'string' || typeof peer.connected !== 'boolean' || typeof peer.measurementStatus !== 'string'
      || !peerNumbers.every(k => nullableNumber(peer[k])) || !number(peer.zdoBatchesReceivedInWindow)) throw new Error('Invalid peer metrics');
    ids.add(peer.peerSessionId);
  }
  if (value.loadedOwnership !== null && (!Array.isArray(value.loadedOwnership) || value.loadedOwnership.length > 10000
    || !value.loadedOwnership.every(x => object(x) && typeof x.ownerSessionId === 'string' && number(x.objects)))) throw new Error('Invalid ownership metrics');
  return value as unknown as Snapshot;
}
