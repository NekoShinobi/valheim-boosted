import { resolve } from 'node:path';
import { TelemetryStore } from './store';
import { handler } from './http';
import { ReportRepository } from './reports';

function integer(name: string, fallback: number, min: number, max: number) {
  const value = Number(process.env[name] ?? fallback);
  if (!Number.isInteger(value) || value < min || value > max) throw new Error(`${name} must be between ${min} and ${max}`);
  return value;
}
const port = integer('PORT', 8080, 1, 65535);
const reports = new ReportRepository(process.env.REPORTS_DIRECTORY ?? resolve('.local/reports'));
await reports.initialize();
const store = new TelemetryStore(
  process.env.TELEMETRY_PATH ?? resolve('.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json'),
  integer('HISTORY_SAMPLES', 300, 1, 3600), integer('STALE_AFTER_MS', 5000, 1000, 300000),
);
await store.poll();
const poller = setInterval(() => { void store.poll(); }, 1000);
const server = Bun.serve({
  hostname: process.env.HOST ?? '127.0.0.1', port,
  fetch: handler(store, resolve(import.meta.dir, '../dist'), reports),
});
console.log(`valheim-boosted metrics: ${server.url}`);
function shutdown() { clearInterval(poller); void server.stop(true); }
process.once('SIGTERM', shutdown);
process.once('SIGINT', shutdown);
