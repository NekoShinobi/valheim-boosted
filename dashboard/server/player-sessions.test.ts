import { test, expect } from 'bun:test';
import { Database } from 'bun:sqlite';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { HistoryDatabase } from './history-database';
import { HistoryService } from './history-service';
import { TelemetryStore } from './store';
import { handler } from './http';
import { historySnapshot } from '../test/history-fixtures';
import { parseSnapshot } from '../shared/telemetry';
import { archiveHistory, archiveSeries } from '../shared/history-view';
import { historyMetrics, validHistoryArchive, type HistoryMetric } from '../shared/history';

const alice = 'a'.repeat(64), bob = 'b'.repeat(64);
function login(at: number, stream: string, playerId = alice, name = 'First character', sequence = 1) {
  const s = historySnapshot(sequence, at), t = s.clientTelemetry!;
  t.identityStatus = 'server_scoped_steam';
  Object.assign(t.peers[0], { streamId: stream, playerId, name, sessionStartedAtMs: at - 10000, lastSeenAtMs: at });
  t.sessions = structuredClone(t.peers);
  Object.assign(t.samples[0], { streamId: stream, playerId });
  return parseSnapshot(s);
}

test('returning accounts aggregate across peer IDs, names and restarts without merging namesakes or counting offline gaps', () => {
  const now = Math.floor(Date.now() / 1000) * 1000, db = new HistoryDatabase(':memory:');
  try {
    const first = login(now - 120000, 'first');
    first.clientTelemetry!.samples[0].sample.frames = 10;
    first.clientTelemetry!.samples[0].sample.frameMeanMs = 100;
    db.ingest(first, now);
    const second = login(now - 20000, 'second', alice, 'Renamed character');
    second.processSession = 'restarted-server'; second.peers[0].peerSessionId = '999';
    second.clientTelemetry!.peers[0].peerSessionId = second.clientTelemetry!.sessions![0].peerSessionId = second.clientTelemetry!.samples[0].peerSessionId = '999';
    second.clientTelemetry!.samples[0].sample.frames = 90;
    second.clientTelemetry!.samples[0].sample.frameMeanMs = 10;
    db.ingest(second, now); db.ingest(second, now); // Repeated exports cannot double-count a received window.
    db.ingest(login(now, 'namesake', bob, 'Renamed character'), now);
    const grouped = db.series(now - 180000, now + 1, now, true);
    expect(grouped.filter(s => s.kind === 'client')).toHaveLength(2);
    expect(grouped.find(s => s.playerId === alice && s.kind === 'client')!.sessionCount).toBe(2);
    const q = (metric: HistoryMetric) => db.query({ fromMs: now - 180000, toMs: now + 1, points: 100, metric, seriesIds: [], playerSeries: [{ playerId: alice, kind: 'client' }] }, now);
    expect(q('frameMeanMs').series[0].summary).toMatchObject({ value: 19, samples: 2, observedMs: 2000 });
    expect(q('fps').series[0].summary!.value).toBe(50);
    expect(q('frames').series[0].summary!.value).toBe(100);
    expect(q('rttMs').series[0].summary!.value).toBe(60);
    expect(q('frameP95Ms').series[0].points).toHaveLength(2);
    const sessions = db.sessions(now - 180000, now + 1, [alice], now);
    expect(sessions).toHaveLength(2);
    expect(sessions.find(s => s.streamId === 'first')).toMatchObject({ name: 'First character', endReason: 'server_changed', endedMs: now - 120000 });
    expect(sessions.find(s => s.streamId === 'second')!.name).toBe('Renamed character');
  } finally { db.close(); }
});

test('explicit disconnects, missed snapshots, reobservation and server shutdown retain truthful session boundaries', () => {
  const now = Date.now(), db = new HistoryDatabase(':memory:');
  try {
    const first = login(now, 'first'); db.ingest(first, now);
    const absent = login(now + 1000, 'unused'); absent.peers = []; absent.clientTelemetry!.peers = []; absent.clientTelemetry!.sessions = []; absent.clientTelemetry!.samples = [];
    db.ingest(absent, now + 1000);
    expect(db.sessions(now - 20000, now + 2000, [alice], now)[0]).toMatchObject({ endedMs: now, endReason: 'not_observed' });
    const reappeared = structuredClone(first); reappeared.clockUtcMs = now + 2000;
    reappeared.clientTelemetry!.peers[0].lastSeenAtMs = reappeared.clientTelemetry!.sessions![0].lastSeenAtMs = now + 2000;
    db.ingest(reappeared, now + 2000);
    expect(db.sessions(now - 20000, now + 3000, [alice], now)[0].endedMs).toBeNull();
    const closed = structuredClone(absent); closed.clockUtcMs = now + 4000;
    closed.clientTelemetry!.sessions = [{ ...reappeared.clientTelemetry!.sessions![0], sessionEndedAtMs: now + 3000 }];
    db.ingest(closed, now + 4000); db.ingest(closed, now + 4000);
    expect(db.sessions(now - 20000, now + 5000, [alice], now)[0]).toMatchObject({ endedMs: now + 3000, lastSeenMs: now + 2000, endReason: 'disconnect' });
    const next = login(now + 6000, 'second'); db.ingest(next, now + 6000);
    db.ingest({ ...next, clockUtcMs: now + 7000, running: false, role: 'stopped' }, now + 7000);
    expect(db.sessions(now - 20000, now + 8000, [alice], now)[0]).toMatchObject({ endedMs: now + 7000, endReason: 'server_stopped' });
    db.cleanup(now + 8 * 86400000);
    expect(db.sessions(now - 20000, now + 8000, [alice], now)).toHaveLength(0);
  } finally { db.close(); }
});

test('saved player aggregates preserve per-metric weights, session records and compatibility with old archives', () => {
  const now = Date.now(), start = now - 7200000, db = new HistoryDatabase(':memory:');
  try {
    for (let i = 0; i < 120; i++) {
      const s = login(start + i * 1000, i < 30 ? 'first' : 'second', alice, i < 30 ? 'Old name' : 'New name', i + 1);
      const w = s.clientTelemetry!.samples[0].sample;
      w.frames = i < 30 ? 10 : 90; w.frameMeanMs = i < 30 ? 100 : 10;
      w.cpuPercent = i % 5 ? null : i; // Weight only observed CPU values, never missing ones.
      db.ingest(s, start + i * 1000);
    }
    // Build stable minute rollups; no extra client sample is introduced.
    const later = login(now, 'later'); later.clientTelemetry!.samples = []; db.ingest(later, now);
    const a = db.archive(start - 10000, now - 1, later.processSession, later.worldSession, now);
    expect(validHistoryArchive(a)).toBe(true);
    const chosen = archiveSeries(a, true).filter(s => s.kind === 'client');
    expect(chosen).toHaveLength(1); expect(chosen[0].sessionCount).toBe(2); expect(a.sessions).toHaveLength(2);
    expect(archiveSeries(a, true, start + 60000, a.toMs).find(s => s.kind === 'client')!.sessionCount).toBe(1);
    for (const metric of ['frameMeanMs', 'fps', 'frames', 'cpuPercent', 'frameP95Ms', 'rttMs'] as HistoryMetric[]) {
      const live = db.query({ fromMs: a.fromMs, toMs: a.toMs, points: 10, metric, seriesIds: [], playerSeries: [{ playerId: alice, kind: 'client' }] }, now);
      const saved = archiveHistory(a, chosen, metric, a.fromMs, a.toMs);
      expect(saved.series[0].summary!.value!).toBeCloseTo(live.series[0].summary!.value!, 8);
      expect(saved.series[0].summary!.observedMs).toBe(120000);
      expect(saved.series[0].summary!.samples).toBe(120);
    }
    const old = structuredClone(a);
    delete old.sessions;
    old.series.forEach(s => { delete s.playerId; delete s.sessionId; });
    old.rows.forEach(r => delete r.weights);
    expect(validHistoryArchive(old)).toBe(true);
    expect(archiveSeries(old, true).filter(s => s.kind === 'client')).toHaveLength(2);
    expect(archiveHistory(old, old.series, 'cpuPercent', old.fromMs, old.toMs).series.every(s => !s.summary)).toBe(true);
    const invalid = structuredClone(a); invalid.rows[0].weights = [NaN]; expect(validHistoryArchive(invalid)).toBe(false);
    invalid.rows[0].weights = Array(historyMetrics.length).fill(-1); expect(validHistoryArchive(invalid)).toBe(false);
  } finally { db.close(); }
});

test('schema v1 migrates in place without guessing identities for older measurements', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-session-migration-')), path = join(directory, 'history.sqlite'), now = Date.now();
  let db = new HistoryDatabase(path);
  try {
    db.ingest(historySnapshot(1, now), now); db.close();
    // Recreate the actual previous schema, including its preserved samples and rollups.
    const old = new Database(path);
    old.exec('DROP INDEX series_player; ALTER TABLE series DROP COLUMN playerId; ALTER TABLE series DROP COLUMN sessionId; DROP TABLE player_sessions; PRAGMA user_version=1;');
    old.close(); db = new HistoryDatabase(path);
    expect(db.series(now - 10000, now + 1000, now, true)).toHaveLength(3);
    expect(db.series(now - 10000, now + 1000, now, true).every(s => s.playerId === null)).toBe(true);
    db.ingest(login(now + 1000, 'new'), now + 1000);
    expect(db.series(now - 10000, now + 2000, now, true)).toHaveLength(5);
    db.close(); db = new HistoryDatabase(path);
    expect(db.sessions(now - 20000, now + 2000, [alice], now)).toHaveLength(1);
    expect(db.status(now).error).toBeNull();
  } finally { db.close(); await rm(directory, { recursive: true }); }
});

test('HTTP player/session queries work through the worker and reject invalid or oversized selectors', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-session-http-'));
  const service = new HistoryService(join(directory, 'history.sqlite')), now = Date.now();
  try {
    await service.initialize(); service.offer(login(now, 'first'), now);
    service.offer(login(now + 1000, 'second', alice, 'Renamed'), now + 1000);
    const fetch = handler(new TelemetryStore('unused'), directory, undefined, service);
    const get = (path: string) => fetch(new Request(`http://localhost/api/history${path}${path.includes('?') ? '&' : '?'}from=${now - 20000}&to=${now + 2000}`));
    const list = await (await get('/series?group=player')).json();
    expect(list.series).toHaveLength(3);
    const sessions = await (await get(`/sessions?players=${alice}`)).json(); expect(sessions.sessions).toHaveLength(2);
    const aggregate = await (await get(`?players=${alice}:client&metric=frames`)).json(); expect(aggregate.series[0].summary.value).toBe(40);
    for (const path of [`?players=${alice}:server`, `?players=${alice}:client:extra`, '?players=bad:client', '/sessions?players=bad', `?players=${Array(10).fill(`${alice}:client`).join(',')}`]) expect((await get(path)).status).toBe(400);
    const bad = login(now, 'bad'); bad.clientTelemetry!.sessions![0].playerId = 'raw-steam-id'; expect(() => parseSnapshot(bad)).toThrow('Invalid client telemetry');
  } finally { await service.close(); await rm(directory, { recursive: true }); }
});
