import type { RecordingView } from './reports';
import { validImprovements, validPeerImprovements, type PeerImprovements, type ServerImprovements } from './improvements';
import { validClientTelemetry, type ClientTelemetry } from './client-telemetry';

export interface Peer {
  improvements?: PeerImprovements | null;
  peerSessionId: string;
  transport: string;
  connected: boolean;
  connectionHealthStatus?: string | null;
  heartbeatAgeSeconds?: number | null;
  applicationQueuedPackets?: number | null;
  applicationQueueNonemptySeconds?: number | null;
  applicationQueueGrowthBytesPerSecond?: number | null;
  replication?: Replication | null;
  measurementStatus: string;
  measurementError?: { exceptionType: string; message: string; operation: string; exceptionChain: string } | null;
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
  p99?: number | null;
  max: number | null;
}

export interface Replication {
  sendAttempts: number; sentBatches: number; noDataOrDeferred: number; sendFailures: number;
  sentZdos: number; receivedPayloadBytes: number | null;
  serviceAgeSeconds: number | null; sendAgeSeconds: number | null;
  sendDurationMs: Timing | null; serviceIntervalMs: Timing | null;
}
export interface Scheduler {
  enabled: boolean; status: string; targetHz: number; maxCallsPerFrame: number; budgetMs: number;
  debtCap: number; eligiblePeers: number; pendingDebt: number; calls: number;
  timeLimitedFrames: number; workLimitedFrames: number; discardedDebt: number; frameWorkMs: Timing | null;
}
export interface Resources {
  status: string; cpuPercentOneCore: number | null; residentBytes: number | null;
  threads: number | null; processorCount: number;
}
export interface Snapshot {
  serverImprovements?: ServerImprovements | null;
  clientTelemetry?: ClientTelemetry | null;
  clockUtcMs?: number | null;
  windowEndMonotonicMs?: number;
  worstFrameEndMonotonicMs?: number | null;
  longFrames250Ms?: number | null;
  gcCollections?: number[];
  clientChangedZdos?: number | null;
  modBuildId?: string | null;
  scheduler?: Scheduler | null;
  resources?: Resources | null;
  longFrames50Ms?: number | null;
  longFrames100Ms?: number | null;
  worldSaving?: boolean | null;
  lastSaveDurationMs?: number | null;
  lastSavePreparationMs?: number | null;
  compatibility?: { gameVersion: string | null; networkVersion: number | null; gameModuleId: string | null; status: string; steamInterface: string | null } | null;
  features?: { id: string; enabled: boolean; status: string; detail: string | null; target: string | null; invocations: number }[] | null;
  schemaVersion: 1;
  modVersion: string;
  processSession: string;
  worldSession: string | null;
  sequence: number;
  capturedAtUtc: string;
  sampleWindowSeconds: number;
  sampleIntervalSeconds?: number | null;
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
  cpuPercentOneCore?: number | null;
  maxServiceAgeMs?: number | null;
  peers: Pick<Peer, 'peerSessionId' | 'rttMs' | 'estimatedTransportQueueMs' | 'outgoingBytesPerSecond' | 'incomingBytesPerSecond'>[];
}
export interface MetricsResponse {
  status: Status;
  message: string;
  lastReadAtUtc: string | null;
  snapshot: Snapshot | null;
  history: HistoryPoint[];
  recording: RecordingView;
}

const object = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x);
const number = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x) && x >= 0;
const nullableNumber = (x: unknown) => x === null || number(x);
const timing = (x: unknown) => x === null || (object(x) && number(x.samples) && number(x.percentileSamples) && ['mean', 'p95', 'max'].every(k => nullableNumber(x[k])) && (x.p99 == null || number(x.p99)));
const peerNumbers = ['rttMs', 'rttSampleDeltaMs', 'localDeliveryQuality', 'remoteDeliveryQuality', 'outgoingBytesPerSecond', 'incomingBytesPerSecond', 'outstandingBytes', 'applicationQueuedBytes', 'pendingReliableBytes', 'pendingUnreliableBytes', 'sentUnacknowledgedReliableBytes', 'estimatedTransportQueueMs', 'estimatedSendRateBytesPerSecond', 'lastZdoBatchReceivedAgoSeconds'];

export function parseSnapshot(value: unknown): Snapshot {
  if (!object(value) || value.schemaVersion !== 1) throw new Error('Unsupported telemetry schema');
  if (value.serverImprovements != null && !validImprovements(value.serverImprovements, timing)) throw new Error('Invalid server improvements');
  if (value.sampleIntervalSeconds != null && (!number(value.sampleIntervalSeconds) || value.sampleIntervalSeconds < 0.5 || value.sampleIntervalSeconds > 10)) throw new Error('Invalid sample interval');
  if (value.clientTelemetry != null && !validClientTelemetry(value.clientTelemetry)) throw new Error('Invalid client telemetry');
  if (!['clockUtcMs', 'windowEndMonotonicMs', 'worstFrameEndMonotonicMs', 'longFrames250Ms', 'clientChangedZdos'].every(k => value[k] == null || number(value[k]))
    || value.gcCollections != null && (!Array.isArray(value.gcCollections) || value.gcCollections.length !== 3 || !value.gcCollections.every(number))) throw new Error('Invalid timeline fields');
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
    if (peer.improvements != null && !validPeerImprovements(peer.improvements)) throw new Error('Invalid peer improvements');
    if (!['heartbeatAgeSeconds', 'applicationQueuedPackets', 'applicationQueueNonemptySeconds'].every(k => peer[k] == null || number(peer[k]))
      || !(peer.applicationQueueGrowthBytesPerSecond == null || typeof peer.applicationQueueGrowthBytesPerSecond === 'number' && Number.isFinite(peer.applicationQueueGrowthBytesPerSecond))
      || !(peer.connectionHealthStatus == null || typeof peer.connectionHealthStatus === 'string')) throw new Error('Invalid connection health');
    if (peer.replication != null) {
      const r = peer.replication;
      if (!object(r) || !['sendAttempts', 'sentBatches', 'noDataOrDeferred', 'sendFailures', 'sentZdos'].every(k => number(r[k]))
        || !['receivedPayloadBytes', 'serviceAgeSeconds', 'sendAgeSeconds'].every(k => nullableNumber(r[k]))
        || !timing(r.sendDurationMs) || !timing(r.serviceIntervalMs)) throw new Error('Invalid replication metrics');
    }
    if (peer.measurementError != null) {
      const error = peer.measurementError;
      if (!object(error) || !['exceptionType', 'message', 'operation', 'exceptionChain']
        .every(k => typeof error[k] === 'string' && error[k].length <= 512)) throw new Error('Invalid measurement error');
    }
  }
  if (!(value.modBuildId == null || typeof value.modBuildId === 'string')
    || !(value.worldSaving == null || typeof value.worldSaving === 'boolean')
    || !['longFrames50Ms', 'longFrames100Ms', 'lastSaveDurationMs', 'lastSavePreparationMs'].every(k => value[k] == null || number(value[k]))) throw new Error('Invalid runtime metrics');
  if (value.resources != null) {
    const r = value.resources;
    if (!object(r) || typeof r.status !== 'string' || !number(r.processorCount)
      || !['cpuPercentOneCore', 'residentBytes', 'threads'].every(k => nullableNumber(r[k]))) throw new Error('Invalid resource metrics');
  }
  if (value.scheduler != null) {
    const s = value.scheduler;
    if (!object(s) || typeof s.status !== 'string' || typeof s.enabled !== 'boolean'
      || !['targetHz', 'maxCallsPerFrame', 'budgetMs', 'debtCap', 'eligiblePeers', 'pendingDebt', 'calls', 'timeLimitedFrames', 'workLimitedFrames', 'discardedDebt'].every(k => number(s[k]))
      || !timing(s.frameWorkMs)) throw new Error('Invalid scheduler metrics');
  }
  if (value.loadedOwnership !== null && (!Array.isArray(value.loadedOwnership) || value.loadedOwnership.length > 10000
    || !value.loadedOwnership.every(x => object(x) && typeof x.ownerSessionId === 'string' && number(x.objects)))) throw new Error('Invalid ownership metrics');
  if (value.compatibility != null) {
    const c = value.compatibility;
    if (!object(c) || typeof c.status !== 'string' || !nullableNumber(c.networkVersion)
      || !['gameVersion', 'gameModuleId', 'steamInterface'].every(k => c[k] === null || typeof c[k] === 'string')) throw new Error('Invalid compatibility report');
  }
  if (value.features != null) {
    if (!Array.isArray(value.features) || value.features.length > 32
      || !value.features.every(f => object(f) && typeof f.id === 'string' && typeof f.enabled === 'boolean'
        && typeof f.status === 'string' && number(f.invocations)
        && ['detail', 'target'].every(k => f[k] === null || typeof f[k] === 'string'))
      || new Set(value.features.map(f => f.id)).size !== value.features.length) throw new Error('Invalid feature report');
  }
  return value as unknown as Snapshot;
}
