import { open } from 'node:fs/promises';
import { parseSnapshot, type Snapshot, type MetricsResponse, type HistoryPoint } from '../shared/telemetry';
import { Recording } from './recording';

export class TelemetryStore {
  private snapshot: Snapshot | null = null;
  private history: HistoryPoint[] = [];
  private acceptedAt = 0;
  private lastReadAt: number | null = null;
  private error: string | null = null;
  private busy = false;
  private recording = new Recording();

  constructor(private readonly path: string, private readonly historyLimit = 300, private readonly staleMs = 5000) {}

  accept(raw: unknown, now = Date.now()) {
    const next = parseSnapshot(raw);
    const previous = this.snapshot;
    const sameSession = previous?.processSession === next.processSession && previous?.worldSession === next.worldSession;
    if (sameSession && next.sequence < previous!.sequence) throw new Error('Telemetry sequence moved backwards');
    this.lastReadAt = now;
    this.error = null;
    if (sameSession && next.sequence === previous!.sequence) return;
    if (!sameSession) this.history = [];
    this.snapshot = next;
    this.acceptedAt = now;
    this.recording.accept(next, this.view(now).status === 'live');
    if (next.running && next.role !== 'menu') {
      this.history.push({
        at: Date.parse(next.capturedAtUtc), frameP95: next.frameIntervalMs?.p95 ?? null,
        networkP95: next.networkUpdateDurationMs?.p95 ?? null,
        cpuPercentOneCore: next.resources?.cpuPercentOneCore ?? null,
        maxServiceAgeMs: next.peers.some(p => p.replication?.serviceAgeSeconds != null)
          ? Math.max(...next.peers.flatMap(p => p.replication?.serviceAgeSeconds == null ? [] : [p.replication.serviceAgeSeconds * 1000])) : null,
        peers: next.peers.map(p => ({ peerSessionId: p.peerSessionId, rttMs: p.rttMs, estimatedTransportQueueMs: p.estimatedTransportQueueMs, outgoingBytesPerSecond: p.outgoingBytesPerSecond, incomingBytesPerSecond: p.incomingBytesPerSecond })),
      });
      if (this.history.length > this.historyLimit) this.history.splice(0, this.history.length - this.historyLimit);
    }
  }

  reset(now = Date.now()) {
    this.history = [];
    this.recording = new Recording(now);
    return this.view(now);
  }

  report(name: string, now = Date.now()) { return this.recording.report(name, this.view(now).status); }

  async poll(now = Date.now()) {
    if (this.busy) return;
    this.busy = true;
    try {
      // Reopen after atomic replacement; limit reads even if an invalid writer grows the file.
      const file = await open(this.path, 'r');
      try {
        const limit = 2 * 1024 * 1024;
        const data = Buffer.alloc(limit + 1);
        let total = 0;
        while (total < data.length) {
          const { bytesRead } = await file.read(data, total, data.length - total, null);
          if (!bytesRead) break;
          total += bytesRead;
        }
        if (total > limit) throw new Error('Snapshot exceeds 2 MiB');
        this.accept(JSON.parse(data.subarray(0, total).toString('utf8')), now);
      } finally { await file.close(); }
    } catch (error) {
      this.error = (error as NodeJS.ErrnoException).code === 'ENOENT' ? 'Snapshot not found' : 'Snapshot could not be read or validated';
    } finally { this.busy = false; }
  }

  view(now = Date.now()): MetricsResponse {
    const snapshot = this.snapshot;
    let status: MetricsResponse['status'] = 'waiting';
    let message = 'Waiting for the mod to export its first snapshot.';
    if (snapshot) {
      const ageLimit = Math.max(this.staleMs, snapshot.sampleWindowSeconds * 3000);
      const stale = now - this.acceptedAt > ageLimit || now - Date.parse(snapshot.capturedAtUtc) > ageLimit || Date.parse(snapshot.capturedAtUtc) - now > 60000;
      status = !snapshot.running || snapshot.role === 'menu' || snapshot.role === 'stopped' ? 'stopped' : this.error || stale ? 'stale' : 'live';
      message = status === 'live' ? 'Receiving telemetry from the mod.' : status === 'stopped' ? 'The game session has stopped.' : 'Showing the last snapshot; telemetry is no longer current.';
    } else if (this.error && this.error !== 'Snapshot not found') {
      status = 'invalid'; message = this.error;
    }
    return { status, message, snapshot, history: this.history, recording: this.recording.view(), lastReadAtUtc: this.lastReadAt === null ? null : new Date(this.lastReadAt).toISOString() };
  }
}
