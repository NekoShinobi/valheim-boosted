import { test, expect } from 'bun:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { HistoryDatabase } from './history-database';
import { HistoryService } from './history-service';
import { historySnapshot } from '../test/history-fixtures';
import { snapshot } from '../test/fixtures';
import { parseSnapshot } from '../shared/telemetry';
import { historyMetrics, validHistoryArchive, type HistoryMetric } from '../shared/history';
import { ReportRepository } from './reports';
import { TelemetryStore } from './store';
import { handler } from './http';

test('history separates client and server perspectives, deduplicates replayed batches and aligns occurrence time', () => {
  const now = Date.now(), db = new HistoryDatabase(':memory:');
  try {
    const s = historySnapshot(1, now); db.ingest(parseSnapshot(s), now); db.ingest(s, now);
    const next = historySnapshot(2, now + 1000); next.clientTelemetry!.samples = s.clientTelemetry!.samples; db.ingest(next, now + 1000);
    const series = db.series(now - 10000, now + 10000, now);
    expect(series).toHaveLength(3);
    const result = db.query({ fromMs: now - 10000, toMs: now + 10000, seriesIds: series.map(s => s.id), metric: 'frameP95Ms', points: 100 }, now);
    const client = result.series.find(s => s.info.kind === 'client')!;
    expect(client.points).toHaveLength(1);
    expect(client.points[0].at).toBe(Math.floor((now - 4000) / 1000) * 1000);
    expect(client.points[0].clockErrorMs).toBe(20);
    expect(client.points[0].value).toBe(55);
    expect(result.series.find(s => s.info.kind === 'server')!.points.reduce((n, p) => n + p.samples, 0)).toBe(2);
    expect(result.series.find(s => s.info.kind === 'connection')!.points[0].value).toBeNull();
    expect(result.markers[0].at).toBe(now - 4100);
    const rtt = db.query({ fromMs: now - 10000, toMs: now + 10000, seriesIds: series.map(s => s.id), metric: 'rttMs', points: 100 }, now);
    expect(rtt.series.find(s => s.info.kind === 'connection')!.points[0].value).toBe(42);
    expect(rtt.series.find(s => s.info.kind === 'client')!.points[0].value).toBe(60);
  } finally { db.close(); }
});

test('seven-day retention expires raw data and summaries while saved report timelines survive', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-history-retention-'));
  const now = Date.now(), old = now - 8 * 86400000;
  const db = new HistoryDatabase(join(directory, 'history.sqlite'));
  try {
    const s = historySnapshot(1, old); db.ingest(s, old);
    const store = new TelemetryStore('unused'); store.accept(s, old);
    const repo = new ReportRepository(join(directory, 'reports')); await repo.initialize();
    const draft = store.report('Archived incident', old);
    draft.timeline = db.archive(old - 10000, old + 1, s.processSession, s.worldSession, old);
    const saved = await repo.save(draft);
    expect(saved.timeline!.rows).toHaveLength(3);
    db.cleanup(now);
    expect(db.series(old - 10000, now, now)).toHaveLength(0);
    expect(db.status(now).earliestMs).toBeNull();
    expect((await repo.get(saved.id))!.timeline!.rows).toHaveLength(3);
    db.ingest(historySnapshot(2, now), now);
    expect(db.status(now).retentionDays).toBe(7);
    expect(db.status(now).latestMs).toBe(now);
  } finally { db.close(); await rm(directory, { recursive: true }); }
});

test('restarts and reconnects retain separate series rather than merging reused peer IDs and sequences', () => {
  const db = new HistoryDatabase(':memory:'), now = Date.now();
  try {
    db.ingest(historySnapshot(1, now), now);
    const second = historySnapshot(1, now + 1000); second.processSession = 'another-process'; db.ingest(second, now + 1000);
    const third = historySnapshot(2, now + 2000); third.clientTelemetry!.peers[0].streamId = 'reconnected'; third.clientTelemetry!.samples[0].streamId = 'reconnected'; db.ingest(third, now + 2000);
    expect(db.series(now - 10000, now + 10000, now).filter(s => s.kind === 'client')).toHaveLength(3);
  } finally { db.close(); }
});

test('minute rollups preserve weighted timing/FPS, nulls, peaks, counts and range boundaries', () => {
  const base = Math.floor(Date.now() / 60000) * 60000 - 3600000, db = new HistoryDatabase(':memory:');
  try {
    for (let i = 1; i <= 180; i++) {
      const s = snapshot(i, base + i * 1000);
      s.frameIntervalMs = { samples: i % 2 ? 1 : 9, percentileSamples: 1, mean: i % 2 ? 100 : 10, p95: i % 2 ? 100 : 20, max: i % 2 ? 100 : 30 };
      s.longFrames50Ms = i % 2 ? 1 : 2;
      db.ingest(s, base + i * 1000);
    }
    // Advance through stable closed minutes so the wide query uses persisted rollups.
    db.ingest(snapshot(181, base + 1800000), base + 1800000);
    const series = db.series(base, base + 1800000, base + 1800000).find(s => s.kind === 'server')!;
    const q = (metric: HistoryMetric, points: number) => db.query({ fromMs: base + 1001, toMs: base + 179999, seriesIds: [series.id], metric, points }, base + 1800000);
    // Force minute resolution with the minimum point limit over a larger sparse range.
    const wide = (metric: HistoryMetric) => db.query({ fromMs: base + 1001, toMs: base + 179999 + 1200000, seriesIds: [series.id], metric, points: 10 }, base + 1800000);
    const mean = q('frameMeanMs', 10).series[0].points;
    expect(mean.reduce((n, p) => n + p.samples, 0)).toBe(178);
    const coarse = wide('frameMeanMs'); expect(coarse.stepMs).toBeGreaterThanOrEqual(60000);
    const values = coarse.series[0].points;
    expect(values.reduce((n, p) => n + p.samples, 0)).toBe(179);
    const archive = db.archive(base + 1001, base + 180000, series.processSession, series.worldSession, base + 1800000);
    const frameIndex = historyMetrics.findIndex(m => m.key === 'frames');
    expect(archive.rows.reduce((n, r) => n + (r.values[frameIndex] ?? 0), 0)).toBe(899);
    expect(wide('frameP95Ms').series[0].points[0].value).toBe(100);
    expect(wide('longFrames50').series[0].points.reduce((n, p) => n + (p.value ?? 0), 0)).toBe(269);
    expect(wide('cpuPercent').series[0].points.every(p => p.value === null)).toBe(true);
    // Within complete balanced minutes, weighted mean = (100*1 + 10*9)/10 = 19.
    expect(values.some(p => p.value != null && Math.abs(p.value - 19) < 0.001)).toBe(true);
    expect(wide('fps').series[0].points.some(p => p.value != null && Math.abs(p.value - 5) < 0.001)).toBe(true);
  } finally { db.close(); }
});

test('history ignores stale/stopped exports and reset does not delete persistent history', () => {
  const db = new HistoryDatabase(':memory:'), now = Date.now();
  try {
    const store = new TelemetryStore('unused', 1, 5000, (s, t) => db.ingest(s, t));
    store.accept(historySnapshot(1, now - 100000), now);
    expect(db.status(now).latestMs).toBeNull();
    store.accept(historySnapshot(2, now), now); store.reset(now);
    expect(db.status(now).latestMs).toBe(now);
    store.accept({ ...historySnapshot(3, now + 1000), running: false }, now + 1000);
    expect(db.status(now).latestMs).toBe(now);
  } finally { db.close(); }
});

test('client telemetry schema rejects unbounded batches, malformed metrics and non-finite timestamps', () => {
  expect(parseSnapshot(historySnapshot()).clientTelemetry!.samples).toHaveLength(1);
  for (const mutate of [
    (s: ReturnType<typeof historySnapshot>) => { s.clientTelemetry!.samples[0].sample.frameMaxMs = Infinity; },
    (s: ReturnType<typeof historySnapshot>) => { s.clientTelemetry!.samples = Array(513).fill(s.clientTelemetry!.samples[0]); },
    (s: ReturnType<typeof historySnapshot>) => { s.clientTelemetry!.samples[0].sample.endMs = -1; },
    (s: ReturnType<typeof historySnapshot>) => { s.clientTelemetry!.peers[0].name = 'x'.repeat(129); },
  ]) { const s = historySnapshot(); mutate(s); expect(() => parseSnapshot(s)).toThrow('Invalid client telemetry'); }
});

test('late handshakes update names and bounded archives retain the server perspective', () => {
  const db = new HistoryDatabase(':memory:'), now = Date.now();
  try {
    const early = historySnapshot(1, now); early.clientTelemetry!.peers[0].name = null; db.ingest(early, now);
    db.ingest(historySnapshot(2, now + 1000), now + 1000);
    expect(db.series(now - 10000, now + 10000, now).filter(s => s.kind !== 'server').every(s => s.name.includes('Player One'))).toBe(true);
    for (let i = 3; i < 75; i++) {
      const s = historySnapshot(i, now + i * 1000); s.clientTelemetry!.samples[0].streamId = `reconnect-${i}`; db.ingest(s, now + i * 1000);
    }
    const a = db.archive(now - 10000, now + 80000, early.processSession, early.worldSession, now + 80000);
    expect(a.seriesTruncated).toBe(true); expect(a.series).toHaveLength(64);
    expect(a.series[0].kind).toBe('server'); expect(a.rows.length).toBeLessThanOrEqual(2600);
    expect(validHistoryArchive(a)).toBe(true);
    const invalid = structuredClone(a); invalid.rows[0].seriesId = 'unknown'; expect(validHistoryArchive(invalid)).toBe(false);
    invalid.rows[0] = { ...a.rows[0], values: [Infinity] }; expect(validHistoryArchive(invalid)).toBe(false);
  } finally { db.close(); }
});

test('history worker persists across restart and HTTP bounds queries and archives reports', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-history-worker-')), path = join(directory, 'history.sqlite');
  let service = new HistoryService(path);
  try {
    await service.initialize();
    const store = new TelemetryStore('unused', 300, 5000, (s, t) => service.offer(s, t));
    const now = Date.now(); store.accept(historySnapshot(1, now), now);
    const repo = new ReportRepository(join(directory, 'reports')); await repo.initialize();
    const fetch = handler(store, directory, repo, service);
    const list = await (await fetch(new Request(`http://localhost/api/history/series?from=${now - 10000}&to=${now + 1000}`))).json();
    expect(list.series).toHaveLength(3);
    expect((await fetch(new Request('http://localhost/api/history?from=no&to=100'))).status).toBe(400);
    expect((await fetch(new Request(`http://localhost/api/history?from=${now - 10000}&to=${now}&metric=evil`))).status).toBe(400);
    expect((await fetch(new Request(`http://localhost/api/history?from=${now - 10000}&to=${now}&points=999999`))).status).toBe(400);
    const response = await fetch(new Request('http://localhost/api/reports', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'With timeline' }) }));
    expect(response.status).toBe(201);
    const saved = await response.json(); expect(saved.timeline.rows.length).toBeGreaterThan(0);
    await service.close(); service = new HistoryService(path); await service.initialize();
    expect((await service.series(now - 10000, now + 1000))).toHaveLength(3);
    expect((await service.status()).error).toBeNull();
  } finally { await service.close(); await rm(directory, { recursive: true }); }
});
