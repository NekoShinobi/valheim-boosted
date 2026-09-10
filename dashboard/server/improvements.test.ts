import { test, expect } from 'bun:test';
import { Database } from 'bun:sqlite';
import { mkdtemp, rm, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseSnapshot } from '../shared/telemetry';
import { historyMetrics, validHistoryArchive } from '../shared/history';
import { archiveHistory } from '../shared/history-view';
import { improvementSnapshot } from '../test/improvement-fixtures';
import { HistoryDatabase } from './history-database';
import { Recording } from './recording';
import { ReportRepository } from './reports';

test('stage metrics reject invalid settings, counts and timing; legacy snapshots remain readable', () => {
  const s = improvementSnapshot();
  expect(parseSnapshot(s).serverImprovements?.framedPayloadBytes).toBe(2048);
  for (const change of [{ maximumWindowBytes: 100000 }, { targetBytesPerSecond: -1 }, { compressionBudgetMs: NaN },
    { compressionEnabled: 1 }, { framedPayloadBytes: 30000 }, { captainTransfers: -1 }, { compressionEncodeMs: {} }])
    expect(() => parseSnapshot({ ...s, serverImprovements: { ...s.serverImprovements, ...change } })).toThrow('server improvements');
  expect(() => parseSnapshot({ ...s, peers: [{ ...s.peers[0], improvements: { ...s.peers[0].improvements, windowBytes: -1 } }] })).toThrow('peer improvements');
  delete s.serverImprovements; delete s.peers[0].improvements; expect(parseSnapshot(s)).toBe(s);
});

test('reports retain settings, weighted compression cost and window counters; changing settings freezes recording', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'vb-improvement-report-'));
  try {
    const a = improvementSnapshot(1, 10000), b = improvementSnapshot(2, 11000), recording = new Recording();
    b.serverImprovements!.compressionEncodeMs!.samples = 8; b.serverImprovements!.compressionEncodeMs!.mean = 0.7;
    b.serverImprovements!.compressedSent = 6;
    recording.accept(a, true); recording.accept(b, true);
    const draft = recording.report('Stage 5', 'live');
    expect(draft.metrics.compressionEncode?.mean).toBeCloseTo(0.6);
    expect(draft.metrics.compressionSaved?.total).toBe(16);
    expect(draft.metrics.compressedSent?.total).toBe(8);
    expect(draft.metrics.compressedSent?.availableSeconds).toBe(2);
    expect(draft.metrics.sendWindow?.mean).toBe(32);
    const changed = improvementSnapshot(3, 12000); changed.serverImprovements!.maximumWindowBytes = 65536;
    recording.accept(changed, true); expect(recording.view().pausedReason).toContain('settings changed');
    const repo = new ReportRepository(dir); const saved = await repo.save(draft); expect(await repo.get(saved.id)).toEqual(saved);
    const path = join(dir, `${saved.id}.json`), old = JSON.parse(await readFile(path, 'utf8')); delete old.source.serverImprovements;
    await writeFile(path, JSON.stringify(old)); expect((await repo.get(saved.id))?.source.serverImprovements).toBeUndefined();
  } finally { await rm(dir, { recursive: true }); }
});

test('v2 histories with 26-value rollups migrate without data loss; appended metrics preserve nulls and archives', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'vb-improvement-history-')); const path = join(dir, 'history.sqlite');
  const now = Date.now(), at = Math.floor((now - 3600000) / 60000) * 60000 + 1000;
  let db = new HistoryDatabase(path);
  try {
    const old = improvementSnapshot(1, at); delete old.serverImprovements; delete old.peers[0].improvements;
    db.ingest(old, at); db.ingest({ ...old, sequence: 2, clockUtcMs: at + 60000, capturedAtUtc: new Date(at + 60000).toISOString() }, at + 60000); db.close();
    const legacy = new Database(path);
    for (const table of ['samples', 'minute_samples']) {
      const rows = legacy.query(`SELECT series_id,${table === 'samples' ? 'seq' : 'bucket'} AS key,v${table === 'samples' ? '' : ',w'} FROM ${table}`).all() as {series_id:string;key:number;v:string;w?:string}[];
      for (const r of rows) legacy.query(`UPDATE ${table} SET v=?${r.w ? ',w=?' : ''} WHERE series_id=? AND ${table === 'samples' ? 'seq' : 'bucket'}=?`)
        .run(JSON.stringify(JSON.parse(r.v).slice(0,26)), ...(r.w ? [JSON.stringify(JSON.parse(r.w).slice(0,26))] : []), r.series_id, r.key);
    }
    legacy.exec('PRAGMA user_version=2;'); legacy.close();
    db = new HistoryDatabase(path);
    const server = db.series(at - 10000, now, now).find(s => s.kind === 'server')!;
    const q = { fromMs: at - 10000, toMs: now, seriesIds: [server.id], metric: 'compressionSavedKiB' as const, points: 10 };
    expect(db.query(q, now).series[0].summary?.value).toBeNull();
    db.ingest(improvementSnapshot(3, now - 1000), now);
    const result = db.query(q, now); expect(result.series[0].summary?.value).toBe(8);
    const archive = db.archive(at - 10000, now, old.processSession, old.worldSession, now);
    expect(validHistoryArchive(archive)).toBe(true); expect(archive.metricLayout).toBe(2);
    const historical = archiveHistory(archive, [server], 'compressionSavedKiB', archive.fromMs, archive.toMs);
    expect(historical.series[0].summary?.value).toBe(8);
    const oldArchive = structuredClone(archive); delete oldArchive.metricLayout;
    for (const row of oldArchive.rows) { row.values = row.values.slice(0, 26); row.weights = row.weights?.slice(0, 26); }
    expect(validHistoryArchive(oldArchive)).toBe(true);
    expect(archiveHistory(oldArchive, [server], 'compressionSavedKiB', archive.fromMs, archive.toMs).series[0].summary?.value).toBeNull();
    expect(historyMetrics.findIndex(m => m.key === 'vSyncCount')).toBe(25);
    const connection = db.series(at - 10000, now, now).find(s => s.kind === 'connection')!;
    expect(db.query({ ...q, seriesIds: [connection.id], metric: 'sendWindowKiB' }, now).series[0].summary?.value).toBe(32);
  } finally { db.close(); await rm(dir, { recursive: true }); }
});
