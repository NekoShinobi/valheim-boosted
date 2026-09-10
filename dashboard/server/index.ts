import { resolve } from 'node:path';
import { TelemetryStore } from './store';
import { handler } from './http';
import { ReportRepository } from './reports';
import { HistoryService } from './history-service';

function integer(name: string, fallback: number, min: number, max: number) {
  const value = Number(process.env[name] ?? fallback);
  if (!Number.isInteger(value) || value < min || value > max) throw new Error(`${name} must be between ${min} and ${max}`);
  return value;
}
const port = integer('PORT', 8080, 1, 65535);
const reports = new ReportRepository(process.env.REPORTS_DIRECTORY ?? resolve('.local/reports'));
await reports.initialize();
const history = new HistoryService(process.env.HISTORY_DATABASE_PATH ?? resolve(process.env.REPORTS_DIRECTORY ?? '.local/reports', 'history.sqlite'),
  integer('HISTORY_RETENTION_DAYS', 7, 1, 365));
await history.initialize().catch(error => console.error('History initialization failed; live metrics remain available:', error));
const store = new TelemetryStore(
  process.env.TELEMETRY_PATH ?? resolve('.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json'),
  integer('HISTORY_SAMPLES', 300, 1, 3600), integer('STALE_AFTER_MS', 5000, 1000, 300000),
  (snapshot, now) => history.offer(snapshot, now),
);
await store.poll();
const poller = setInterval(() => { void store.poll(); }, 1000);
const server = Bun.serve({
  hostname: process.env.HOST ?? '127.0.0.1', port,
  fetch: handler(store, resolve(import.meta.dir, '../dist'), reports, history),
});
console.log(`valheim-boosted metrics: ${server.url}`);
function shutdown() {
  clearInterval(poller); void server.stop(true);
  const timeout = setTimeout(() => process.exit(0), 5000);
  void history.close().finally(() => { clearTimeout(timeout); process.exit(0); });
}
process.once('SIGTERM', shutdown);
process.once('SIGINT', shutdown);
