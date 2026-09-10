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

test('schema validation rejects malformed data and keeps unavailable values null', () => {
  expect(() => parseSnapshot({ ...snapshot(), schemaVersion: 9 })).toThrow();
  expect(() => parseSnapshot({ ...snapshot(), peers: [{ peerSessionId: 'x' }] })).toThrow();
  const value = snapshot(); value.peers = [peer()];
  expect(parseSnapshot(value).peers[0]!.rttSampleDeltaMs).toBeNull();
  value.peers[0]!.rttMs = Number.NaN;
  expect(() => parseSnapshot(value)).toThrow();
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
