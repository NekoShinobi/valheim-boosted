import type { Snapshot, Status } from './telemetry';
import type { HistoryArchive } from './history';
import type { ImprovementSettings } from './improvements';

export const reportMetrics = [
  { id: 'sendWindow', label: 'Peer ZDO allowance · mean', unit: 'KiB', mode: 'mean' },
  { id: 'steamMaxRate', label: 'Peer Steam maximum rate · mean', unit: 'KiB/s', mode: 'mean' },
  { id: 'steamRateFailures', label: 'Steam rate failures · largest connection total', unit: '', mode: 'max' },
  { id: 'compressionEncode', label: 'Compression encode · mean', unit: 'ms', mode: 'mean' },
  { id: 'compressionDecode', label: 'Compression decode · mean', unit: 'ms', mode: 'mean' },
  { id: 'compressionSaved', label: 'Compressed ZDO bytes saved · all peers', unit: 'KiB/s', mode: 'rate' },
  { id: 'compressedSent', label: 'Compressed ZDO frames sent', unit: '/s', mode: 'rate' },
  { id: 'compressedReceived', label: 'Compressed ZDO frames received', unit: '/s', mode: 'rate' },
  { id: 'compressionRejected', label: 'Compression messages rejected', unit: '/s', mode: 'rate' },
  { id: 'compressionSkipped', label: 'Negotiated sends using vanilla', unit: '/s', mode: 'rate' },
  { id: 'captainTransfers', label: 'Ship ownership transfers', unit: '/s', mode: 'rate' },
  { id: 'captainDeferred', label: 'Ship transfers deferred by work limits', unit: '/s', mode: 'rate' },
  { id: 'peers', label: 'Connected peers', unit: '', mode: 'mean' },
  { id: 'loadedObjects', label: 'Loaded objects', unit: '', mode: 'mean' },
  { id: 'knownZdos', label: 'Known ZDOs', unit: '', mode: 'mean' },
  { id: 'frameMean', label: 'Frame interval · mean', unit: 'ms', mode: 'mean' },
  { id: 'frameP95', label: 'Frame interval · worst window p95', unit: 'ms', mode: 'max' },
  { id: 'frameP99', label: 'Frame interval · worst window p99', unit: 'ms', mode: 'max' },
  { id: 'frameMax', label: 'Frame interval · maximum', unit: 'ms', mode: 'max' },
  { id: 'networkMean', label: 'ZDO update · mean', unit: 'ms', mode: 'mean' },
  { id: 'networkP95', label: 'ZDO update · worst window p95', unit: 'ms', mode: 'max' },
  { id: 'cpu', label: 'CPU · mean (100% = one core)', unit: '%', mode: 'mean' },
  { id: 'rss', label: 'Resident memory · peak', unit: 'MiB', mode: 'max' },
  { id: 'managedMemory', label: 'Managed memory · peak', unit: 'MiB', mode: 'max' },
  { id: 'longFrames50', label: 'Frames ≥50 ms', unit: '/s', mode: 'rate' },
  { id: 'longFrames100', label: 'Frames ≥100 ms', unit: '/s', mode: 'rate' },
  { id: 'rtt', label: 'Peer RTT · mean', unit: 'ms', mode: 'mean' },
  { id: 'rttMax', label: 'Peer RTT · maximum', unit: 'ms', mode: 'max' },
  { id: 'queueDelay', label: 'Peer transport queue delay · mean', unit: 'ms', mode: 'mean' },
  { id: 'queueDelayMax', label: 'Peer transport queue delay · maximum', unit: 'ms', mode: 'max' },
  { id: 'applicationQueue', label: 'Peer application queue · peak', unit: 'KiB', mode: 'max' },
  { id: 'outgoing', label: 'Peer outgoing traffic · mean', unit: 'KiB/s', mode: 'mean' },
  { id: 'incoming', label: 'Peer incoming traffic · mean', unit: 'KiB/s', mode: 'mean' },
  { id: 'heartbeatAge', label: 'Peer heartbeat age · maximum', unit: 'ms', mode: 'max' },
  { id: 'serviceAge', label: 'Peer replication service age · maximum', unit: 'ms', mode: 'max' },
  { id: 'serviceInterval', label: 'Replication service interval · mean', unit: 'ms', mode: 'mean' },
  { id: 'sentBatches', label: 'Replication batches sent · all peers', unit: '/s', mode: 'rate' },
  { id: 'sentZdos', label: 'Replicated ZDOs · all peers', unit: '/s', mode: 'rate' },
  { id: 'sendFailures', label: 'Replication send failures · all peers', unit: '/s', mode: 'rate' },
  { id: 'schedulerWork', label: 'Scheduler frame work · mean', unit: 'ms', mode: 'mean' },
  { id: 'timeLimited', label: 'Scheduler time-limited frames', unit: '/s', mode: 'rate' },
  { id: 'workLimited', label: 'Scheduler work-limited frames', unit: '/s', mode: 'rate' },
  { id: 'discardedDebt', label: 'Scheduler discarded credits', unit: '/s', mode: 'rate' },
] as const;
export type MetricId = typeof reportMetrics[number]['id'];
export interface Aggregate {
  observations: number; windows: number; weight: number; mean: number;
  min: number; max: number; total: number; availableSeconds: number;
}
export interface ReportSource {
  serverImprovements?: ImprovementSettings | null;
  processSession: string; worldSession: string | null; role: string;
  modVersion: string; modBuildId: string | null;
  compatibility: Snapshot['compatibility'];
  scheduler: Pick<NonNullable<Snapshot['scheduler']>, 'enabled' | 'targetHz' | 'budgetMs' | 'maxCallsPerFrame' | 'debtCap'> | null;
  features: { id: string; enabled: boolean }[];
}
export interface RecordingView {
  startedAtUtc: string | null; endedAtUtc: string | null; snapshots: number;
  observedSeconds: number; elapsedSeconds: number; missedSnapshots: number;
  peerObservations: number; unavailablePeerObservations: number; schedulerActiveSnapshots: number;
  pausedReason: string | null;
}
export interface ReportDraft extends RecordingView {
  timeline?: HistoryArchive | null;
  timelineError?: string | null;
  name: string; source: ReportSource;
  metrics: Partial<Record<MetricId, Aggregate>>;
  telemetryStatus: Status;
}
export interface SavedReport extends ReportDraft { reportVersion: 1; id: string; savedAtUtc: string }
export type ReportSummary = Pick<SavedReport, 'id' | 'name' | 'savedAtUtc' | 'startedAtUtc' | 'endedAtUtc' | 'snapshots' | 'observedSeconds'>;

export function metricValue(report: SavedReport, definition: typeof reportMetrics[number]): number | null {
  const value = report.metrics[definition.id];
  if (!value) return null;
  return definition.mode === 'max' ? value.max : definition.mode === 'rate'
    ? value.availableSeconds > 0 ? value.total / value.availableSeconds : null : value.mean;
}

export function comparisonWarnings(a: SavedReport, b: SavedReport): string[] {
  const warnings: string[] = [];
  if (a.id === b.id) warnings.push('Choose two different reports to compare a change.');
  if (a.source.role !== b.source.role) warnings.push('These reports measure different process roles.');
  if (JSON.stringify(a.source.compatibility) !== JSON.stringify(b.source.compatibility)) warnings.push('Game compatibility details differ. Check the game version, protocol and module before attributing a change to the mod.');
  if (!a.source.modBuildId || !b.source.modBuildId) warnings.push('At least one report lacks a mod build ID.');
  if (a.source.worldSession !== b.source.worldSession) warnings.push('World sessions differ. Session IDs cannot confirm that the same save, location or workload was used.');
  if (a.observedSeconds < 60 || b.observedSeconds < 60) warnings.push('At least one recording has less than a minute of observed data.');
  if (Math.max(a.observedSeconds, b.observedSeconds) > Math.min(a.observedSeconds, b.observedSeconds) * 1.2) warnings.push('Recording lengths differ by more than 20%; peaks and worst-window percentiles depend on duration.');
  if (a.metrics.peers?.mean !== b.metrics.peers?.mean || a.metrics.peers?.max !== b.metrics.peers?.max) warnings.push('Connected-player counts differ. Match player count and activity for a useful baseline.');
  for (const report of [a, b]) {
    if (!report.peerObservations) warnings.push(`${report.name}: no connected peers were observed; this cannot assess multiplayer networking performance.`);
    if (report.missedSnapshots || report.elapsedSeconds > report.observedSeconds * 1.1) warnings.push(`${report.name}: gaps in collection; counters include only observed windows.`);
    if (report.unavailablePeerObservations) warnings.push(`${report.name}: some peer transport measurements were unavailable.`);
  }
  return warnings;
}
