import { validPlayerId } from './client-telemetry';

export const historyMetrics = [
  { key: 'frameMeanMs', label: 'Frame interval · mean', unit: 'ms', rollup: 'frames' },
  { key: 'frameP95Ms', label: 'Frame interval · worst window p95', unit: 'ms', rollup: 'max' },
  { key: 'frameP99Ms', label: 'Frame interval · worst window p99', unit: 'ms', rollup: 'max' },
  { key: 'frameMaxMs', label: 'Frame interval · maximum', unit: 'ms', rollup: 'max' },
  { key: 'fps', label: 'FPS · mean', unit: 'fps', rollup: 'fps' },
  { key: 'frames', label: 'Observed frames', unit: 'frames', rollup: 'sum' },
  { key: 'longFrames50', label: 'Frames ≥50 ms', unit: 'frames/bucket', rollup: 'sum' },
  { key: 'longFrames100', label: 'Frames ≥100 ms', unit: 'frames/bucket', rollup: 'sum' },
  { key: 'longFrames250', label: 'Frames ≥250 ms', unit: 'frames/bucket', rollup: 'sum' },
  { key: 'gc0', label: 'GC collections · generation 0', unit: 'collections/bucket', rollup: 'sum' },
  { key: 'gc1', label: 'GC collections · generation 1', unit: 'collections/bucket', rollup: 'sum' },
  { key: 'gc2', label: 'GC collections · generation 2', unit: 'collections/bucket', rollup: 'sum' },
  { key: 'cpuPercent', label: 'CPU · 100% = one core', unit: '%', rollup: 'mean' },
  { key: 'managedMemoryMiB', label: 'Managed memory', unit: 'MiB', rollup: 'max' },
  { key: 'networkP95Ms', label: 'ZDO update · worst window p95', unit: 'ms', rollup: 'max' },
  { key: 'rttMs', label: 'RTT · mean', unit: 'ms', rollup: 'mean' },
  { key: 'queueMs', label: 'Transport queue delay · maximum', unit: 'ms', rollup: 'max' },
  { key: 'serviceAgeMs', label: 'Replication service age · maximum', unit: 'ms', rollup: 'max' },
  { key: 'changedZdos', label: 'Changed ZDO backlog', unit: 'objects', rollup: 'max' },
  { key: 'residentMemoryMiB', label: 'Resident memory', unit: 'MiB', rollup: 'max' },
  { key: 'loadedObjects', label: 'Loaded objects', unit: 'objects', rollup: 'mean' },
  { key: 'worldSaving', label: 'World save active', unit: '0 / 1', rollup: 'max' },
  { key: 'focused', label: 'Client focused · all windows', unit: '0 / 1', rollup: 'min' },
  { key: 'loading', label: 'Local player unavailable', unit: '0 / 1', rollup: 'max' },
  { key: 'frameLimit', label: 'Configured frame limit (−1 = default)', unit: 'fps', rollup: 'max' },
  { key: 'vSyncCount', label: 'VSync count', unit: '', rollup: 'max' },
  // Append-only metric layout 2. Historical layout 1 has the first 26 entries.
  { key: 'sendWindowKiB', label: 'ZDO send allowance', unit: 'KiB', rollup: 'mean' },
  { key: 'steamMaxRateKiBs', label: 'Steam maximum send rate', unit: 'KiB/s', rollup: 'mean' },
  { key: 'steamRateFailures', label: 'Steam rate failures · connection total', unit: 'failures', rollup: 'max' },
  { key: 'compressionSavedKiB', label: 'Compressed ZDO bytes saved', unit: 'KiB/bucket', rollup: 'sum' },
  { key: 'compressedSent', label: 'Compressed ZDO frames sent', unit: 'frames/bucket', rollup: 'sum' },
  { key: 'compressedReceived', label: 'Compressed ZDO frames received', unit: 'frames/bucket', rollup: 'sum' },
  { key: 'compressionRejected', label: 'Compression messages rejected', unit: 'messages/bucket', rollup: 'sum' },
  { key: 'compressionEncodeP95Ms', label: 'Compression encode · worst window p95', unit: 'ms', rollup: 'max' },
  { key: 'compressionDecodeP95Ms', label: 'Compression decode · worst window p95', unit: 'ms', rollup: 'max' },
  { key: 'captainTransfers', label: 'Ship ownership transfers', unit: 'transfers/bucket', rollup: 'sum' },
] as const;
export type HistoryMetric = typeof historyMetrics[number]['key'];
export type HistoryValues = Partial<Record<HistoryMetric, number | null>>;
export interface HistorySeries {
  id: string; processSession: string; worldSession: string | null; peerSessionId: string | null;
  kind: 'server' | 'client' | 'connection'; name: string; modBuildId: string | null; metadata: string;
  firstMs: number; lastMs: number;
  playerId?: string | null; sessionId?: string | null; grouped?: boolean; sessionCount?: number;
}
export interface PlayerSession {
  id: string; playerId: string | null; streamId: string; processSession: string; worldSession: string | null;
  peerSessionId: string; name: string; startedMs: number; lastSeenMs: number; endedMs: number | null;
  endReason: 'disconnect' | 'not_observed' | 'server_changed' | 'server_stopped' | null;
}
export interface PlayerSeriesTarget { playerId: string; kind: 'client' | 'connection' }
export interface HistorySummary { value: number | null; samples: number; observedMs: number; clockErrorMs: number | null }
export interface HistoryRow {
  seriesId: string; sequence: number; startMs: number; endMs: number; receivedAtMs: number;
  clockErrorMs: number | null; markerMs: number | null; worstFrameEndMs: number | null;
  values: (number | null)[];
  observedMs?: number; samples?: number;
  weights?: number[];
}
export interface HistoryQuery { fromMs: number; toMs: number; seriesIds: string[]; playerSeries?: PlayerSeriesTarget[]; metric: HistoryMetric; points: number }
export interface HistoryResult {
  retainedFromMs?: number;
  fromMs: number; toMs: number; stepMs: number; metric: HistoryMetric; retentionDays: number;
  series: { info: HistorySeries; summary?: HistorySummary; points: ({ at: number } & HistorySummary)[] }[];
  markers: { seriesId: string; at: number; clockErrorMs: number | null }[];
  markersTruncated: boolean;
}
export interface HistoryArchive {
  metricLayout?: 2;
  version: 1; fromMs: number; toMs: number; stepMs: number; series: HistorySeries[];
  rows: HistoryRow[]; seriesTruncated: boolean;
  retainedFromMs?: number;
  sessions?: PlayerSession[];
}
export interface HistoryStatus {
  retentionDays: number; earliestMs: number | null; latestMs: number | null;
  databaseBytes: number; error: string | null; droppedOffers: number;
}
export const packedValues = (v: HistoryValues) => historyMetrics.map(m => v[m.key] ?? null);

export function validHistoryArchive(v: unknown): v is HistoryArchive {
  const object = (x: unknown): x is Record<string, unknown> => !!x && typeof x === 'object' && !Array.isArray(x);
  const num = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x) && x >= 0 && x <= Number.MAX_SAFE_INTEGER;
  const nullable = (x: unknown) => x === null || num(x);
  const str = (x: unknown) => typeof x === 'string' && x.length <= 192;
  if (!object(v) || v.version !== 1 || v.metricLayout != null && v.metricLayout !== 2 || !num(v.fromMs) || !num(v.toMs) || v.toMs <= v.fromMs || !num(v.stepMs) || v.stepMs < 1000
    || v.retainedFromMs != null && !num(v.retainedFromMs) || typeof v.seriesTruncated !== 'boolean'
    || !Array.isArray(v.series) || v.series.length > 64 || !Array.isArray(v.rows) || v.rows.length > 2600) return false;
  if (!v.series.every(s => object(s) && typeof s.id === 'string' && /^[a-f0-9]{32}$/.test(s.id)
    && str(s.processSession) && str(s.name) && ['worldSession', 'peerSessionId', 'modBuildId'].every(k => s[k] === null || str(s[k]))
    && ['server', 'client', 'connection'].includes(String(s.kind)) && typeof s.metadata === 'string' && s.metadata.length <= 16384 && num(s.firstMs) && num(s.lastMs))) return false;
  if (!v.series.every(s => (s.playerId == null || validPlayerId(s.playerId)) && (s.sessionId == null || str(s.sessionId)))) return false;
  if (v.sessions != null && (!Array.isArray(v.sessions) || v.sessions.length > 256 || !v.sessions.every(s => object(s) && str(s.id) && str(s.streamId)
    && (s.playerId === null || validPlayerId(s.playerId)) && str(s.processSession) && (s.worldSession === null || str(s.worldSession)) && str(s.peerSessionId) && str(s.name)
    && num(s.startedMs) && num(s.lastSeenMs) && s.lastSeenMs >= s.startedMs && nullable(s.endedMs)
    && (s.endedMs === null || Number(s.endedMs) >= s.lastSeenMs)
    && (s.endReason === null || ['disconnect', 'not_observed', 'server_changed', 'server_stopped'].includes(String(s.endReason)))
    && (s.endedMs === null) === (s.endReason === null)))) return false;
  if (Array.isArray(v.sessions) && new Set(v.sessions.map(s => s.id)).size !== v.sessions.length) return false;
  const ids = new Set(v.series.map(s => s.id));
  if (ids.size !== v.series.length) return false;
  return v.rows.every(r => object(r) && ids.has(r.seriesId) && Number.isSafeInteger(r.sequence) && Number(r.sequence) > 0
    && ['startMs', 'endMs', 'receivedAtMs'].every(k => num(r[k])) && Number(r.endMs) > Number(r.startMs)
    && ['clockErrorMs', 'markerMs', 'worstFrameEndMs'].every(k => nullable(r[k]))
    && ['samples', 'observedMs'].every(k => r[k] == null || num(r[k]))
    && Array.isArray(r.values)
    && (r.weights == null || Array.isArray(r.weights) && r.weights.length === (r.values as unknown[]).length && r.weights.every(num))
    && Array.isArray(r.values) && (r.values.length === historyMetrics.length || v.metricLayout == null && r.values.length === 26) && r.values.every((n, i) => nullable(n) || historyMetrics[i].key === 'frameLimit' && n === -1));
}
