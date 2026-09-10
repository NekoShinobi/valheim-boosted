import type { Snapshot } from '../shared/telemetry';
import type { HistoryArchive, HistoryQuery, HistoryResult, HistorySeries, HistoryStatus, PlayerSession } from '../shared/history';

export interface HistoryReader {
  query(q: HistoryQuery): Promise<HistoryResult>;
  series(fromMs: number, toMs: number,groupPlayers?:boolean): Promise<HistorySeries[]>;
  sessions(fromMs:number,toMs:number,playerIds?:string[]): Promise<PlayerSession[]>;
  status(): Promise<HistoryStatus>;
  archive(fromMs: number, toMs: number, processSession: string, worldSession: string | null): Promise<HistoryArchive>;
}

export class HistoryService implements HistoryReader {
  private readonly worker = new Worker(new URL('./history-worker.ts', import.meta.url).href);
  private calls = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private nextId = 0;
  private busy = false;
  private pending: { snapshot: Snapshot; now: number } | null = null;
  private failed: string | null = null;
  private fatal: string | null = null;
  private dropped = 0;
  private closed = false;
  private maintenance: ReturnType<typeof setInterval> | undefined;
  private cache: HistoryStatus;
  constructor(private readonly path: string, private readonly retentionDays = 7) {
    this.cache = { retentionDays, earliestMs: null, latestMs: null, databaseBytes: 0, error: null, droppedOffers: 0 };
    this.worker.onmessage = (event: MessageEvent) => {
      const call = this.calls.get(event.data.id);
      if (!call) return;
      this.calls.delete(event.data.id);
      if (event.data.error) call.reject(new Error(event.data.error)); else call.resolve(event.data.value);
    };
    this.worker.onerror = (event: ErrorEvent) => {
      this.fatal = this.failed = event.message || 'History worker failed';
      for (const call of this.calls.values()) call.reject(new Error(this.failed));
      this.calls.clear();
    };
  }
  private request<T>(action: string, args: unknown = {}): Promise<T> {
    if (this.fatal) return Promise.reject(new Error(this.fatal));
    if (this.closed || this.calls.size >= 16) return Promise.reject(new Error('History service is busy or closed'));
    const id = ++this.nextId;
    return new Promise<T>((resolve, reject) => {
      this.calls.set(id, { resolve: v => resolve(v as T), reject });
      try { this.worker.postMessage({ id, action, args }); }
      catch (e) { this.calls.delete(id); reject(e); }
    });
  }
  async initialize() {
    this.cache = await this.request<HistoryStatus>('initialize', { path: this.path, retentionDays: this.retentionDays });
    this.maintenance = setInterval(() => { if (!this.busy && this.calls.size < 2) void this.request('cleanup').catch(e => { this.failed = e.message; }); }, 60000);
  }
  offer(snapshot: Snapshot, now: number) {
    if (this.closed) return;
    if (this.pending) this.dropped++;
    this.pending = { snapshot, now };
    if (!this.busy) void this.drain();
  }
  private async drain() {
    this.busy = true;
    try {
      while (this.pending && !this.closed) {
        const args = this.pending; this.pending = null;
        try { await this.request('ingest', args); this.failed = null; }
        catch (e) { this.failed = e instanceof Error ? e.message : 'History write failed'; }
      }
    } finally { this.busy = false; }
  }
  query(q: HistoryQuery) { return this.request<HistoryResult>('query', q); }
  series(fromMs: number, toMs: number,groupPlayers=false) { return this.request<HistorySeries[]>('series', { fromMs, toMs,groupPlayers }); }
  sessions(fromMs:number,toMs:number,playerIds?:string[]) { return this.request<PlayerSession[]>('sessions',{fromMs,toMs,playerIds}); }
  async archive(fromMs: number, toMs: number, processSession: string, worldSession: string | null) {
    if (this.pending) { const args = this.pending; this.pending = null; await this.request('ingest', args); }
    return this.request<HistoryArchive>('archive', { fromMs, toMs, processSession, worldSession });
  }
  async status() {
    try { this.cache = await this.request<HistoryStatus>('status'); }
    catch (e) { this.failed = e instanceof Error ? e.message : 'History unavailable'; }
    return { ...this.cache, error: this.failed, droppedOffers: this.dropped };
  }
  async close() {
    if (this.closed) return;
    clearInterval(this.maintenance);
    // Queue behind any in-flight database operation; shutdown has a bounded wait in index.ts.
    try { if (this.pending) { const args = this.pending; this.pending = null; await this.request('ingest', args); } await this.request('close'); }
    finally { this.closed = true; this.worker.terminate(); }
  }
}
