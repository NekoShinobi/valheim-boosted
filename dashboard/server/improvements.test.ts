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
    expect(validHistoryArchive(archive)).toBe(true); expect(archive.metricLayout).toBe(3);
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


test('network additions validate, aggregate window counts and preserve layout-2 archives and v3 databases', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'vb-network-history-')); const path = join(dir, 'history.sqlite');
  const now = Date.now(), at = now - 3600000;
  let db = new HistoryDatabase(path);
  try {
    const old = improvementSnapshot(1, at); db.ingest(old, at);
    const archive2 = db.archive(at - 1000, at + 1000, old.processSession, old.worldSession, at + 1000);
    archive2.metricLayout = 2;
    for (const row of archive2.rows) { row.values = row.values.slice(0, 36); row.weights = row.weights?.slice(0, 36); }
    expect(validHistoryArchive(archive2)).toBe(true);
    db.close(); const legacy = new Database(path); legacy.exec('PRAGMA user_version=3;'); legacy.close();
    db = new HistoryDatabase(path);
    const s = improvementSnapshot(2, now - 1000);
    s.serverImprovements!.network = { freshPositions: 10, positionFallbacks: 5, actorBonuses: 100, vanillaPriorityPasses: 5,
      earlyBuffered: 4, earlyReplayed: 3, earlyFailures: 0, earlyQueuedBytes: 100, forcedSharing: true, mapCapablePeers: 1,
      mapSentBytes: 512, mapReceivedBytes: 14, mapPackets: 2, mapSkipped: 0, mapRejected: 0 };
    expect(parseSnapshot(s)).toBe(s);
    for (const changes of [{ mapSentBytes: -1 }, { mapCapablePeers: 65 }, { earlyQueuedBytes: 2097153 }, { forcedSharing: 1 }, { actorBonuses: NaN }])
      expect(() => parseSnapshot({ ...s, serverImprovements: { ...s.serverImprovements, network: { ...s.serverImprovements!.network, ...changes } } })).toThrow('server improvements');
    db.ingest(s, now); const next = structuredClone(s); next.sequence++; next.clockUtcMs = now; next.capturedAtUtc = new Date(now).toISOString();
    next.serverImprovements!.network!.mapSentBytes = 256; db.ingest(next, now);
    const server = db.series(at - 1000, now, now).find(row => row.kind === 'server')!;
    const query = { fromMs: at - 1000, toMs: now, seriesIds: [server.id], metric: 'mapSentBytes' as const, points: 10 };
    expect(db.query(query, now).series[0].summary?.value).toBe(768);
    expect(archiveHistory(archive2, archive2.series, 'mapSentBytes', archive2.fromMs, archive2.toMs).series[0].summary?.value).toBeNull();
    const archive3 = db.archive(at - 1000, now, old.processSession, old.worldSession, now);
    expect(archive3.metricLayout).toBe(3); expect(validHistoryArchive(archive3)).toBe(true);
    expect(archiveHistory(archive3, [server], 'mapSentBytes', archive3.fromMs, archive3.toMs).series[0].summary?.value).toBe(768);
    const recording = new Recording(); recording.accept(s, true); recording.accept(next, true);
    const report = recording.report('Network additions', 'live');
    expect(report.metrics.mapSentBytes?.total).toBe(768); expect(report.metrics.mapSentBytes?.availableSeconds).toBe(2);
    expect(report.metrics.earlyQueuedBytes?.max).toBe(100); expect(report.metrics.actorBonuses?.total).toBe(200);
    const repository = new ReportRepository(dir); const saved = await repository.save(report);
    expect((await repository.get(saved.id))?.metrics.mapSentBytes?.total).toBe(768);
  } finally { db.close(); await rm(dir, { recursive: true }); }
});
