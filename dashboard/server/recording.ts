import type { Peer, Snapshot, Status, Timing } from '../shared/telemetry';
import type { Aggregate, MetricId, RecordingView, ReportDraft, ReportSource } from '../shared/reports';

function source(snapshot: Snapshot): ReportSource {
  const s = snapshot.scheduler;
  return {
    processSession: snapshot.processSession, worldSession: snapshot.worldSession, role: snapshot.role,
    modVersion: snapshot.modVersion, modBuildId: snapshot.modBuildId ?? null,
    compatibility: snapshot.compatibility ?? null,
    scheduler: s ? { enabled: s.enabled, targetHz: s.targetHz, budgetMs: s.budgetMs, maxCallsPerFrame: s.maxCallsPerFrame, debtCap: s.debtCap } : null,
    features: (snapshot.features ?? []).map(f => ({ id: f.id, enabled: f.enabled })).sort((a, b) => a.id.localeCompare(b.id)),
  };
}

/** Fixed-size accumulators, independent of the rolling chart history. */
export class Recording {
  private first: ReportSource | null = null;
  private signature = '';
  private lastSequence = 0;
  private metrics: Partial<Record<MetricId, Aggregate>> = {};
  private state: RecordingView = {
    startedAtUtc: null, endedAtUtc: null, snapshots: 0, observedSeconds: 0, elapsedSeconds: 0,
    missedSnapshots: 0, peerObservations: 0, unavailablePeerObservations: 0,
    schedulerActiveSnapshots: 0, pausedReason: null,
  };
  constructor(private readonly after: number | null = null) {}

  accept(snapshot: Snapshot, fresh: boolean) {
    if (this.state.pausedReason) return;
    const metadata = source(snapshot);
    if (this.first && (JSON.stringify(metadata) !== this.signature || !snapshot.running || ['menu', 'stopped'].includes(snapshot.role))) {
      this.state.pausedReason = 'Session or settings changed. Save this window, then reset stats to record the current session.';
      return;
    }
    const end = Date.parse(snapshot.capturedAtUtc);
    const start = end - snapshot.sampleWindowSeconds * 1000;
    // Skip stale files and partial windows straddling a reset. Never count the same sequence twice.
    if (!fresh || !snapshot.running || ['menu', 'stopped'].includes(snapshot.role) || snapshot.sampleWindowSeconds <= 0
      || this.after !== null && start < this.after || this.first && (snapshot.sequence <= this.lastSequence || end <= Date.parse(this.state.endedAtUtc!))) return;
    if (!this.first) {
      this.first = metadata; this.signature = JSON.stringify(metadata);
      this.state.startedAtUtc = new Date(start).toISOString();
    } else this.state.missedSnapshots += snapshot.sequence - this.lastSequence - 1;
    this.lastSequence = snapshot.sequence;
    this.state.endedAtUtc = snapshot.capturedAtUtc;
    this.state.snapshots++;
    this.state.observedSeconds += snapshot.sampleWindowSeconds;
    this.state.elapsedSeconds = (end - Date.parse(this.state.startedAtUtc!)) / 1000;
    const peers = snapshot.peers.filter(p => p.connected);
    this.state.peerObservations += peers.length;
    this.state.unavailablePeerObservations += peers.filter(p => p.measurementStatus !== 'available').length;
    if (snapshot.scheduler?.status === 'active') this.state.schedulerActiveSnapshots++;
    const touched = new Set<MetricId>();
    const add = (id: MetricId, value: number | null | undefined, weight = 1) => {
      if (value == null || weight <= 0) return;
      const metric = this.metrics[id] ??= { observations: 0, windows: 0, weight: 0, mean: 0, min: value, max: value, total: 0, availableSeconds: 0 };
      metric.observations++; metric.weight += weight;
      metric.mean += (value - metric.mean) * weight / metric.weight;
      metric.min = Math.min(metric.min, value); metric.max = Math.max(metric.max, value); metric.total += value * weight;
      if (!touched.has(id)) { metric.windows++; metric.availableSeconds += snapshot.sampleWindowSeconds; touched.add(id); }
    };
    const timing = (id: MetricId, t: Timing | null | undefined) => add(id, t?.mean, t?.samples ?? 0);
    const perPeer = (id: MetricId, read: (p: Peer) => number | null | undefined, scale = 1) => {
      for (const p of peers) { const value = read(p); if (value != null) add(id, value * scale); }
    };
    add('peers', peers.length); add('loadedObjects', snapshot.loadedObjects); add('knownZdos', snapshot.knownZdos);
    timing('frameMean', snapshot.frameIntervalMs); add('frameP95', snapshot.frameIntervalMs?.p95);
    add('frameP99', snapshot.frameIntervalMs?.p99); add('frameMax', snapshot.frameIntervalMs?.max);
    timing('networkMean', snapshot.networkUpdateDurationMs); add('networkP95', snapshot.networkUpdateDurationMs?.p95);
    add('cpu', snapshot.resources?.cpuPercentOneCore);
    add('rss', snapshot.resources?.residentBytes == null ? null : snapshot.resources.residentBytes / 1048576);
    add('managedMemory', snapshot.managedMemoryBytes / 1048576);
    add('longFrames50', snapshot.longFrames50Ms); add('longFrames100', snapshot.longFrames100Ms);
    perPeer('rtt', p => p.rttMs); perPeer('rttMax', p => p.rttMs);
    perPeer('queueDelay', p => p.estimatedTransportQueueMs); perPeer('queueDelayMax', p => p.estimatedTransportQueueMs);
    perPeer('applicationQueue', p => p.applicationQueuedBytes, 1 / 1024);
    perPeer('outgoing', p => p.outgoingBytesPerSecond, 1 / 1024); perPeer('incoming', p => p.incomingBytesPerSecond, 1 / 1024);
    perPeer('heartbeatAge', p => p.heartbeatAgeSeconds, 1000); perPeer('serviceAge', p => p.replication?.serviceAgeSeconds, 1000);
    for (const p of peers) timing('serviceInterval', p.replication?.serviceIntervalMs);
    perPeer('sentBatches', p => p.replication?.sentBatches); perPeer('sentZdos', p => p.replication?.sentZdos);
    perPeer('sendFailures', p => p.replication?.sendFailures);
    timing('schedulerWork', snapshot.scheduler?.frameWorkMs);
    add('timeLimited', snapshot.scheduler?.timeLimitedFrames); add('workLimited', snapshot.scheduler?.workLimitedFrames);
    add('discardedDebt', snapshot.scheduler?.discardedDebt);
  }

  view(): RecordingView { return { ...this.state }; }
  report(name: string, telemetryStatus: Status): ReportDraft {
    if (!this.first) throw new Error('No complete, fresh telemetry windows recorded yet.');
    return structuredClone({ ...this.state, name, source: this.first, metrics: this.metrics, telemetryStatus });
  }
}
