import { test, expect } from 'bun:test';
import { mkdtemp, writeFile, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { TelemetryStore } from './store';
import { parseSnapshot } from '../shared/telemetry';
import { snapshot, peer } from '../test/fixtures';

test('missing snapshot is waiting, not healthy', async () => {
  const store = new TelemetryStore('/nonexistent/valheim-boosted/snapshot.json');
  await store.poll();
  expect(store.view().status).toBe('waiting');
  expect(store.view().snapshot).toBeNull();
});

test('compatibility reports survive validation and malformed probe reports are rejected', () => {
  const value = { ...snapshot(), compatibility: { gameVersion: '1.0.7', networkVersion: 39, gameModuleId: 'module', status: 'supported', steamInterface: null },
    features: [{ id: 'NetworkTiming', enabled: true, status: 'fingerprint_mismatch', detail: 'changed target', target: 'ZDOMan.Update', invocations: 0 }] };
  expect(parseSnapshot(value).features?.[0]?.status).toBe('fingerprint_mismatch');
  expect(() => parseSnapshot({ ...value, features: [...value.features, ...value.features] })).toThrow();
  expect(() => parseSnapshot({ ...value, compatibility: { ...value.compatibility, networkVersion: '39' } })).toThrow();
  expect(parseSnapshot(snapshot()).compatibility).toBeUndefined();
});

test('schema validation rejects malformed data and keeps unavailable values null', () => {
  expect(() => parseSnapshot({ ...snapshot(), schemaVersion: 9 })).toThrow();
  expect(() => parseSnapshot({ ...snapshot(), peers: [{ peerSessionId: 'x' }] })).toThrow();
  const value = snapshot(); value.peers = [peer()];
  expect(parseSnapshot(value).peers[0]!.rttSampleDeltaMs).toBeNull();
  value.peers[0]!.rttMs = Number.NaN;
  expect(() => parseSnapshot(value)).toThrow();
});

test('measurement error details survive validation and clear on recovery; older snapshots work', () => {
  const value = snapshot(); value.peers = [peer()];
  expect(parseSnapshot(value).peers[0]!.measurementError).toBeUndefined();
  const details = { exceptionType: 'DllNotFoundException', message: 'native library missing', operation: 'Steamworks.Query', exceptionChain: 'TargetInvocationException -> DllNotFoundException' };
  value.peers[0]!.measurementStatus = 'unavailable:DllNotFoundException';
  value.peers[0]!.measurementError = details;
  const store = new TelemetryStore('unused');
  store.accept(value);
  expect(store.view().snapshot!.peers[0]!.measurementError).toEqual(details);
  for (const bad of [{ ...details, message: 42 }, { ...details, operation: 'x'.repeat(513) }]) {
    expect(() => parseSnapshot({ ...value, peers: [{ ...peer(), measurementError: bad }] })).toThrow('Invalid measurement error');
  }
  const recovered = snapshot(value.sequence + 1); recovered.peers = [{ ...peer(), measurementError: null }];
  store.accept(recovered);
  expect(store.view().snapshot!.peers[0]!.measurementError).toBeNull();
});

test('unchanged files become stale even when repeatedly read', () => {
  const store = new TelemetryStore('unused');
  store.accept(snapshot(1, 10000), 10000);
  expect(store.view(11000).status).toBe('live');
  store.accept(snapshot(1, 10000), 17000);
  expect(store.view(17000).status).toBe('stale');
  expect(store.view(17000).history).toHaveLength(1);
});

test('old file is immediately stale on dashboard startup', () => {
  const store = new TelemetryStore('unused'); store.accept(snapshot(1, 10000), 20000);
  expect(store.view(20000).status).toBe('stale');
});

test('bounded history resets on world changes and rejects sequence regression', () => {
  const store = new TelemetryStore('unused', 2);
  for (let i = 1; i <= 5; i++) store.accept(snapshot(i, i * 1000), i * 1000);
  expect(store.view(5000).history).toHaveLength(2);
  expect(() => store.accept(snapshot(1), 6000)).toThrow();
  store.accept({ ...snapshot(1, 6000), worldSession: 'new-world' }, 6000);
  expect(store.view(6000).history).toHaveLength(1);
  store.accept({ ...snapshot(1, 7000), processSession: 'new-process' }, 7000);
  expect(store.view(7000).history).toHaveLength(1);
});

test('shutdown snapshots show stopped and do not fabricate chart measurements', () => {
  const store = new TelemetryStore('unused');
  store.accept({ ...snapshot(), running: false, role: 'stopped', frameIntervalMs: null, networkUpdateDurationMs: null });
  expect(store.view().status).toBe('stopped');
  expect(store.view().history).toHaveLength(0);
});

test('reader handles atomic replacement, invalid data, oversize files and recovery', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'valheim-boosted-'));
  const path = join(directory, 'snapshot.json');
  const store = new TelemetryStore(path);
  try {
    await writeFile(path, JSON.stringify(snapshot(1))); await store.poll();
    await writeFile(path + '.tmp', JSON.stringify(snapshot(2))); await rename(path + '.tmp', path); await store.poll();
    expect(store.view().snapshot?.sequence).toBe(2);
    await writeFile(path, '{broken'); await store.poll();
    expect(store.view().status).toBe('stale');
    expect(store.view().snapshot?.sequence).toBe(2);
    await writeFile(path, ' '.repeat(2 * 1024 * 1024 + 1)); await store.poll();
    expect(store.view().status).toBe('stale');
    await writeFile(path, JSON.stringify(snapshot(3))); await store.poll();
    expect(store.view().status).toBe('live');
  } finally { await rm(directory, { recursive: true }); }
});


test('stage 2 metrics preserve units, signed queue growth, history, and reject invalid fields', () => {
  const value = snapshot();
  value.modBuildId = 'fixture-build';
  value.resources = { status: 'available', cpuPercentOneCore: 175, residentBytes: 256 * 1048576, threads: 40, processorCount: 4 };
  value.scheduler = { enabled: true, status: 'active', targetHz: 20, maxCallsPerFrame: 4, budgetMs: 2, debtCap: 2, eligiblePeers: 1, pendingDebt: 1, calls: 20, timeLimitedFrames: 1, workLimitedFrames: 0, discardedDebt: 3, frameWorkMs: value.networkUpdateDurationMs };
  value.worldSaving = false; value.longFrames50Ms = 2; value.longFrames100Ms = 1;
  value.peers = [{ ...peer(), heartbeatAgeSeconds: 0.2, applicationQueueGrowthBytesPerSecond: -100,
    replication: { sendAttempts: 20, sentBatches: 3, noDataOrDeferred: 16, sendFailures: 1, sentZdos: 50, receivedPayloadBytes: 1024, serviceAgeSeconds: 0.1, sendAgeSeconds: 0.2, sendDurationMs: value.networkUpdateDurationMs, serviceIntervalMs: value.frameIntervalMs } }];
  const store = new TelemetryStore('unused'); store.accept(value);
  expect(store.view().history[0]!.maxServiceAgeMs).toBe(100);
  expect(store.view().history[0]!.cpuPercentOneCore).toBe(175);
  expect(store.view().snapshot!.peers[0]!.applicationQueueGrowthBytesPerSecond).toBe(-100);
  expect(parseSnapshot(snapshot()).scheduler).toBeUndefined();
  expect(() => parseSnapshot({ ...value, scheduler: { ...value.scheduler, pendingDebt: -1 } })).toThrow();
  expect(() => parseSnapshot({ ...value, resources: { ...value.resources, cpuPercentOneCore: NaN } })).toThrow();
  expect(() => parseSnapshot({ ...value, peers: [{ ...value.peers[0], replication: { ...value.peers[0]!.replication, serviceAgeSeconds: -1 } }] })).toThrow();
  const legacy = new TelemetryStore('unused'); legacy.accept(snapshot());
  expect(legacy.view().history[0]!.maxServiceAgeMs).toBeNull();
});
