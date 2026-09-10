import { test, expect } from 'bun:test';
import { mkdtemp, mkdir, rm, writeFile, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { TelemetryStore } from './store';
import { ReportRepository } from './reports';
import { handler } from './http';
import { snapshot, peer } from '../test/fixtures';
import { comparisonWarnings, metricValue, reportMetrics, type SavedReport } from '../shared/reports';

test('reports aggregate all unique windows beyond chart retention with event-weighted means and honest percentiles', () => {
  const store = new TelemetryStore('unused', 1);
  const first = snapshot(1, 1000);
  first.frameIntervalMs = { samples: 1, percentileSamples: 1, mean: 100, p95: 100, max: 100 };
  first.peers = [peer()]; first.longFrames50Ms = 2;
  store.accept(first, 1000);
  store.accept(first, 1500);
  const second = snapshot(3, 4000);
  second.sampleWindowSeconds = 2;
  second.frameIntervalMs = { samples: 9, percentileSamples: 9, mean: 10, p95: 20, max: 30 };
  second.peers = [{ ...peer(), rttMs: null, measurementStatus: 'unavailable' }];
  second.longFrames50Ms = 4;
  store.accept(second, 4000);
  const report = store.report('Baseline', 4000);
  expect(store.view(4000).history).toHaveLength(1);
  expect(report.snapshots).toBe(2);
  expect(report.metrics.frameMean?.mean).toBe(19);
  expect(report.metrics.frameMean?.total).toBe(190);
  expect(report.metrics.frameP95?.max).toBe(100);
  expect(report.metrics.frameP95?.observations).toBe(2);
  expect(report.metrics.rtt?.mean).toBe(42);
  expect(report.metrics.rtt?.windows).toBe(1);
  expect(report.metrics.cpu).toBeUndefined();
  expect(report.observedSeconds).toBe(3);
  expect(report.elapsedSeconds).toBe(4);
  expect(report.missedSnapshots).toBe(1);
  expect(report.unavailablePeerObservations).toBe(1);
  const saved = { ...report, id: crypto.randomUUID(), savedAtUtc: new Date().toISOString(), reportVersion: 1 as const };
  expect(metricValue(saved, reportMetrics.find(m => m.id === 'longFrames50')!)).toBe(2);
  expect(metricValue(saved, reportMetrics.find(m => m.id === 'cpu')!)).toBeNull();
});

test('reset excludes duplicate and overlapping windows without changing the game snapshot or prior report', () => {
  const store = new TelemetryStore('unused');
  store.accept(snapshot(1, 1000), 1000);
  const report = store.report('Before', 1000);
  store.reset(1500);
  expect(store.view(1500).snapshot?.sequence).toBe(1);
  expect(store.view(1500).recording.snapshots).toBe(0);
  expect(store.view(1500).history).toHaveLength(0);
  store.accept(snapshot(1, 1000), 1600);
  store.accept(snapshot(2, 2000), 2000);
  expect(() => store.report('Empty', 2000)).toThrow('No complete');
  store.accept(snapshot(3, 3000), 3000);
  expect(store.report('After', 3000).snapshots).toBe(1);
  expect(store.report('After', 3000).startedAtUtc).toBe(new Date(2000).toISOString());
  expect(report.snapshots).toBe(1);
  expect(report.endedAtUtc).toBe(new Date(1000).toISOString());
});

test('stale, stopped and menu snapshots do not create report measurements', () => {
  for (const value of [snapshot(1, 1000), { ...snapshot(1, 10000), running: false }, { ...snapshot(1, 10000), role: 'menu' }]) {
    const store = new TelemetryStore('unused'); store.accept(value, 10000);
    expect(() => store.report('Empty', 10000)).toThrow('No complete');
  }
});

test('a session or configuration change freezes the prior recording until explicitly reset', () => {
  for (const changed of [{ processSession: 'new-process' }, { worldSession: 'new-world' }, { modBuildId: 'new-build' },
    { features: [{ id: 'SteamTransport', enabled: false, status: 'disabled', detail: null, target: null, invocations: 0 }] }]) {
    const store = new TelemetryStore('unused'); store.accept(snapshot(1, 1000), 1000);
    store.accept({ ...snapshot(2, 2000), ...changed }, 2000);
    expect(store.view(2000).recording.pausedReason).toContain('Session or settings changed');
    expect(store.report('Prior', 2000).snapshots).toBe(1);
    expect(store.report('Prior', 2000).source.processSession).toBe('test-process');
    store.reset(2000);
    store.accept({ ...snapshot(3, 3000), ...changed }, 3000);
    expect(store.view(3000).recording.pausedReason).toBeNull();
    expect(store.view(3000).recording.snapshots).toBe(1);
  }
});

test('window-counter rates sum peers and divide by observed time once per window', () => {
  const store = new TelemetryStore('unused');
  const value = snapshot(1, 2000); value.sampleWindowSeconds = 2;
  const replication = { sendAttempts: 2, sentBatches: 2, noDataOrDeferred: 0, sendFailures: 0, sentZdos: 5,
    receivedPayloadBytes: 100, serviceAgeSeconds: 0.25, sendAgeSeconds: 0.5, sendDurationMs: null, serviceIntervalMs: value.frameIntervalMs };
  value.peers = [{ ...peer(), replication }, { ...peer(), peerSessionId: 'second', replication }];
  store.accept(value, 2000);
  const report = store.report('Two peers', 2000);
  expect(report.metrics.sentBatches?.total).toBe(4);
  expect(report.metrics.sentBatches?.availableSeconds).toBe(2);
  expect(report.metrics.serviceAge?.max).toBe(250);
  expect(report.metrics.serviceInterval?.weight).toBe(120);
});

test('reports persist across repository restart, stay immutable after reset, and survive a corrupt sibling', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-reports-'));
  try {
    const store = new TelemetryStore('unused'); store.accept(snapshot(1, 1000), 1000);
    const repository = new ReportRepository(directory); await repository.initialize();
    const saved = await repository.save(store.report('Vanilla / before', 1000));
    store.reset(1000); store.accept(snapshot(2, 2000), 2000);
    const reopened = new ReportRepository(directory);
    expect(await reopened.get(saved.id)).toEqual(saved);
    expect((await reopened.list()).reports[0]?.name).toBe('Vanilla / before');
    expect(await reopened.get('../snapshot')).toBeNull();
    const brokenId = crypto.randomUUID(); await writeFile(join(directory, `${brokenId}.json`), '{}');
    expect((await reopened.list()).unreadable).toBe(1);
    expect((await reopened.list()).reports).toHaveLength(1);
    await writeFile(join(directory, `${brokenId}.json`), 'x'.repeat(1024 * 1024 + 1));
    await expect(reopened.get(brokenId)).rejects.toThrow('size limit');
    expect((await readdir(directory)).filter(f => f.endsWith('.tmp'))).toHaveLength(0);
  } finally { await rm(directory, { recursive: true }); }
});

test('simultaneous saves honor the storage limit without deleting existing reports', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-reports-limit-'));
  try {
    const store = new TelemetryStore('unused'); store.accept(snapshot());
    const repo = new ReportRepository(directory, 1);
    const results = await Promise.allSettled([repo.save(store.report('A')), repo.save(store.report('B'))]);
    expect(results.map(r => r.status)).toEqual(['fulfilled', 'rejected']);
    expect((await repo.list()).reports).toHaveLength(1);
  } finally { await rm(directory, { recursive: true }); }
});

test('report HTTP saves, lists, downloads and resets, with bounded JSON and same-origin mutations', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'vb-reports-http-'));
  try {
    const publicPath = join(directory, 'public'); await mkdir(publicPath);
    await writeFile(join(publicPath, 'index.html'), 'report application');
    const repo = new ReportRepository(join(directory, 'reports')); await repo.initialize();
    const store = new TelemetryStore('unused'); const fetch = handler(store, publicPath, repo);
    const post = (path: string, body: unknown, headers: Record<string, string> = {}) => fetch(new Request(`http://localhost${path}`, {
      method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json', Origin: 'http://localhost', ...headers },
    }));
    expect((await post('/api/reports', { name: 'Empty' })).status).toBe(409);
    store.accept(snapshot());
    expect((await post('/api/reports', { name: 'Blocked' }, { Origin: 'https://attacker.invalid' })).status).toBe(403);
    expect((await post('/api/recording/reset', {}, { 'Sec-Fetch-Site': 'cross-site' })).status).toBe(403);
    expect(store.view().recording.snapshots).toBe(1);
    expect((await post('/api/reports', { name: 'Wrong type' }, { 'Content-Type': 'text/plain' })).status).toBe(415);
    for (const body of [{ name: '' }, { name: 'a'.repeat(101) }, { name: 'line\nbreak' }, { name: 'x'.repeat(5000) }, []])
      expect((await post('/api/reports', body)).status).toBe(400);
    const response = await post('/api/reports', { name: 'Baseline' }, { Origin: 'https://localhost' }); expect(response.status).toBe(201);
    const saved: SavedReport = await response.json();
    expect((await (await fetch(new Request('http://localhost/api/reports'))).json()).reports).toHaveLength(1);
    const download = await fetch(new Request(`http://localhost/api/reports/${saved.id}/download`));
    expect(download.headers.get('Content-Disposition')).toContain('attachment;');
    expect(await download.json()).toEqual(saved);
    expect((await fetch(new Request(`http://localhost/api/reports/${crypto.randomUUID()}`))).status).toBe(404);
    expect(await (await fetch(new Request('http://localhost/reports'))).text()).toBe('report application');
    expect((await fetch(new Request(`http://localhost/reports/${saved.id}.json`))).status).toBe(404);
    expect((await fetch(new Request(`http://localhost/api/reports/${saved.id}`, { method: 'DELETE' }))).status).toBe(405);
    const reset = await post('/api/recording/reset', {});
    expect((await reset.json()).recording.snapshots).toBe(0);
    expect((await repo.list()).reports).toHaveLength(1);
  } finally { await rm(directory, { recursive: true }); }
});

test('comparison flags differing workloads, missing measurements and gaps', () => {
  const store = new TelemetryStore('unused'); store.accept(snapshot());
  const a: SavedReport = { ...store.report('A'), reportVersion: 1, id: crypto.randomUUID(), savedAtUtc: new Date().toISOString() };
  const b = structuredClone(a); b.id = crypto.randomUUID(); b.name = 'B';
  b.metrics.peers!.mean = 2; b.missedSnapshots = 3; b.unavailablePeerObservations = 1;
  const warnings = comparisonWarnings(a, b).join(' ');
  expect(warnings).toContain('player counts differ');
  expect(warnings).toContain('gaps in collection');
  expect(warnings).toContain('transport measurements were unavailable');
  expect(warnings).toContain('less than a minute');
});
