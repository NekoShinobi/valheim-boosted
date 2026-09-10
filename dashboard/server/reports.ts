import { mkdir, open, readdir, rename, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { reportMetrics, type ReportDraft, type ReportSummary, type SavedReport } from '../shared/reports';
import { validHistoryArchive } from '../shared/history';
import { validImprovementSettings } from '../shared/improvements';

const validId = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const fileLimit = 4 * 1024 * 1024;
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const nonnegative = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v) && v >= 0;
const textOrNull = (v: unknown) => v === null || typeof v === 'string';
const timestamp = (v: unknown) => typeof v === 'string' && Number.isFinite(Date.parse(v));
function validSource(v: unknown): boolean {
  if (!object(v) || !['processSession', 'role', 'modVersion'].every(k => typeof v[k] === 'string')
    || !textOrNull(v.worldSession) || !textOrNull(v.modBuildId) || v.serverImprovements != null && !validImprovementSettings(v.serverImprovements)
    || !Array.isArray(v.features) || v.features.length > 32
    || !v.features.every(f => object(f) && typeof f.id === 'string' && typeof f.enabled === 'boolean')) return false;
  const c = v.compatibility, s = v.scheduler;
  return (c === null || object(c) && typeof c.status === 'string'
    && ['gameVersion', 'gameModuleId', 'steamInterface'].every(k => textOrNull(c[k]))
    && (c.networkVersion === null || nonnegative(c.networkVersion)))
    && (s === null || object(s) && typeof s.enabled === 'boolean'
      && ['targetHz', 'budgetMs', 'maxCallsPerFrame', 'debtCap'].every(k => nonnegative(s[k])));
}
export class ReportRepository {
  private writing: Promise<unknown> = Promise.resolve();
  constructor(private readonly directory: string, private readonly limit = 1000) {}
  async initialize() { await mkdir(this.directory, { recursive: true }); }

  async get(id: string): Promise<SavedReport | null> {
    if (!validId.test(id)) return null;
    let file;
    try { file = await open(join(this.directory, `${id}.json`), 'r'); }
    catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null; throw error; }
    try {
      const size = (await file.stat()).size;
      if (size > fileLimit) throw new Error('Report exceeds size limit');
      const buffer = Buffer.alloc(size + 1);
      let length = 0;
      while (length < buffer.length) {
        const { bytesRead } = await file.read(buffer, length, buffer.length - length, null);
        if (!bytesRead) break;
        length += bytesRead;
      }
      if (length > fileLimit) throw new Error('Report exceeds size limit');
      if (length > size) throw new Error('Report changed while reading');
      const value = JSON.parse(buffer.subarray(0, length).toString('utf8'));
      // Reports are server-generated. Reject damaged or incompatible stored files.
      if (!object(value) || value.reportVersion !== 1 || value.id !== id || typeof value.name !== 'string'
        || !value.name.trim() || value.name.length > 100 || !timestamp(value.savedAtUtc) || !timestamp(value.startedAtUtc) || !timestamp(value.endedAtUtc)
        || !['snapshots', 'observedSeconds', 'elapsedSeconds', 'missedSnapshots', 'peerObservations', 'unavailablePeerObservations', 'schedulerActiveSnapshots'].every(k => nonnegative(value[k]))
        || !(Number(value.snapshots) > 0) || !(Number(value.observedSeconds) > 0) || !textOrNull(value.pausedReason)
        || !['waiting', 'live', 'stale', 'stopped', 'invalid'].includes(String(value.telemetryStatus))
        || !validSource(value.source) || !object(value.metrics)
        || value.timeline != null && !validHistoryArchive(value.timeline)
        || value.timelineError != null && (typeof value.timelineError !== 'string' || value.timelineError.length > 1024)
        || !Object.entries(value.metrics).every(([key, m]) => reportMetrics.some(d => d.id === key) && object(m) &&
          ['observations', 'windows', 'weight', 'mean', 'min', 'max', 'total', 'availableSeconds'].every(k => nonnegative(m[k])))) {
        throw new Error('Invalid saved report');
      }
      return value as unknown as SavedReport;
    } finally { await file.close(); }
  }

  async list(): Promise<{ reports: ReportSummary[]; unreadable: number }> {
    const names = (await readdir(this.directory)).filter(name => validId.test(name.replace(/\.json$/, '')) && name.endsWith('.json'));
    if (names.length > this.limit) throw new Error('Report storage limit exceeded; archive older files from the reports directory.');
    const reports: ReportSummary[] = [];
    let unreadable = 0;
    for (const name of names) {
      try {
        const report = await this.get(name.slice(0, -5));
        if (report) {
          const { id, name, savedAtUtc, startedAtUtc, endedAtUtc, snapshots, observedSeconds } = report;
          reports.push({ id, name, savedAtUtc, startedAtUtc, endedAtUtc, snapshots, observedSeconds });
        }
      } catch { unreadable++; }
    }
    return { reports: reports.sort((a, b) => b.savedAtUtc.localeCompare(a.savedAtUtc)), unreadable };
  }

  save(draft: ReportDraft): Promise<SavedReport> {
    // Serialize saves so simultaneous requests cannot overrun the storage limit.
    const operation = this.writing.then(async () => {
      const names = await readdir(this.directory);
      if (names.filter(n => n.endsWith('.json')).length >= this.limit) throw new Error('Report limit reached; archive older JSON files from the reports directory.');
      const report: SavedReport = { ...draft, reportVersion: 1, id: crypto.randomUUID(), savedAtUtc: new Date().toISOString() };
      const json = JSON.stringify(report, null, 2) + '\n';
      if (Buffer.byteLength(json) > fileLimit) throw new Error('Report exceeds size limit');
      const temp = join(this.directory, `${report.id}.tmp`);
      try {
        await writeFile(temp, json, { flag: 'wx', mode: 0o600 });
        await rename(temp, join(this.directory, `${report.id}.json`));
      } finally { await rm(temp, { force: true }); }
      return report;
    });
    this.writing = operation.catch(() => {});
    return operation;
  }
}
