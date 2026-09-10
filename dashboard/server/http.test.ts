import { test, expect } from 'bun:test';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { handler } from './http';
import { TelemetryStore } from './store';

test('HTTP exposes metrics and public files, but not sibling files or write methods', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'valheim-boosted-http-'));
  const publicPath = join(directory, 'public');
  const { mkdir } = await import('node:fs/promises'); await mkdir(publicPath);
  await writeFile(join(publicPath, 'index.html'), '<h1>Dashboard</h1>');
  await writeFile(join(directory, 'secret.txt'), 'not public');
  const fetch = handler(new TelemetryStore('unused'), publicPath);
  try {
    expect(await (await fetch(new Request('http://localhost/'))).text()).toContain('Dashboard');
    expect((await (await fetch(new Request('http://localhost/api/metrics'))).json()).status).toBe('waiting');
    expect((await fetch(new Request('http://localhost/%2e%2e%2fsecret.txt'))).status).toBe(404);
    expect((await fetch(new Request('http://localhost/api/metrics', { method: 'POST' }))).status).toBe(405);
    expect((await fetch(new Request('http://localhost/healthz'))).status).toBe(200);
  } finally { await rm(directory, { recursive: true }); }
});
