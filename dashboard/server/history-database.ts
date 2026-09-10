import { Database } from 'bun:sqlite';
import { createHash } from 'node:crypto';
import { improvementSettings } from '../shared/improvements';
import type { Snapshot } from '../shared/telemetry';
import { PlayerSessions, playerLabel, sessionId } from './player-sessions';
import { historyMetrics, packedValues, type HistoryArchive, type HistoryQuery, type HistoryResult, type HistoryRow, type HistorySeries, type HistoryStatus, type HistoryValues, type PlayerSeriesTarget, type HistorySummary } from '../shared/history';

const id = (...parts: unknown[]) => createHash('sha256').update(JSON.stringify(parts)).digest('hex').slice(0, 32);
const frameIndex = historyMetrics.findIndex(m => m.key === 'frames');
const valueSql = (index: number) => `json_extract(v, '$[${index}]')`;
function aggregate(index: number) {
  const d = historyMetrics[index], v = valueSql(index), frames = valueSql(frameIndex);
  if (d.rollup === 'frames') return `1.0 * SUM(${v} * ${frames}) / NULLIF(SUM(CASE WHEN ${v} IS NOT NULL THEN ${frames} END), 0)`;
  if (d.rollup === 'fps') return `1000.0 * SUM(${frames}) / NULLIF(SUM(CASE WHEN ${frames} IS NOT NULL THEN end_ms - start_ms END), 0)`;
  return `${d.rollup === 'mean' ? 'AVG' : d.rollup.toUpperCase()}(${v})`;
}
function weightSql(index: number) {
  const d = historyMetrics[index], v = valueSql(index);
  return `CASE WHEN ${v} IS NULL THEN 0 ELSE ${d.rollup === 'frames' ? valueSql(frameIndex) : d.rollup === 'fps' ? 'end_ms-start_ms' : '1'} END`;
}
function rolledAggregate(index: number) {
  const d = historyMetrics[index], v = valueSql(index), w = `json_extract(w,'$[${index}]')`;
  return ['mean', 'frames', 'fps'].includes(d.rollup) ? `1.0 * SUM(${v} * ${w}) / NULLIF(SUM(${w}),0)` : `${d.rollup.toUpperCase()}(${v})`;
}

/** Runs in the history worker in production. SQLite never runs inside Valheim. */
export class HistoryDatabase {
  private readonly db: Database;
  private lastCleanup = 0;
  private lastRollup = 0;
  private readonly playerSessions: PlayerSessions;
  constructor(path: string, readonly retentionDays = 7) {
    if (!Number.isInteger(retentionDays) || retentionDays < 1 || retentionDays > 365) throw new Error('History retention must be 1–365 days');
    this.db = new Database(path, { create: true, strict: true });
    const version = (this.db.query('PRAGMA user_version').get() as { user_version: number }).user_version;
    if (version > 3) { this.db.close(); throw new Error('History database is newer than this service'); }
    this.db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000; PRAGMA cache_size=-8192; PRAGMA temp_store=FILE;');
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS series (
        id TEXT PRIMARY KEY, processSession TEXT NOT NULL, worldSession TEXT, peerSessionId TEXT,
        kind TEXT NOT NULL, name TEXT NOT NULL, modBuildId TEXT, metadata TEXT NOT NULL,
        firstMs REAL NOT NULL, lastMs REAL NOT NULL
      );
      CREATE TABLE IF NOT EXISTS samples (
        series_id TEXT NOT NULL, seq INTEGER NOT NULL, start_ms REAL NOT NULL, end_ms REAL NOT NULL,
        received_ms REAL NOT NULL, clock_error REAL, marker_ms REAL, worst_ms REAL, v TEXT NOT NULL,
        PRIMARY KEY(series_id, seq)
      ) WITHOUT ROWID;
      CREATE INDEX IF NOT EXISTS sample_time ON samples(end_ms);
      CREATE INDEX IF NOT EXISTS sample_series_time ON samples(series_id, end_ms);
      CREATE INDEX IF NOT EXISTS sample_markers ON samples(series_id, marker_ms) WHERE marker_ms IS NOT NULL;
      CREATE INDEX IF NOT EXISTS series_time ON series(lastMs);
      CREATE TABLE IF NOT EXISTS minute_samples (
        series_id TEXT NOT NULL, bucket REAL NOT NULL, start_ms REAL NOT NULL, end_ms REAL NOT NULL,
        received_ms REAL NOT NULL, clock_error REAL, marker_ms REAL, worst_ms REAL, v TEXT NOT NULL,
        w TEXT NOT NULL, observed_ms REAL NOT NULL, n INTEGER NOT NULL, PRIMARY KEY(series_id,bucket)
      ) WITHOUT ROWID;
      CREATE INDEX IF NOT EXISTS minute_time ON minute_samples(bucket);
      CREATE TABLE IF NOT EXISTS history_meta (key TEXT PRIMARY KEY, value REAL NOT NULL);
    `);
    this.db.transaction(() => {
      if (version < 2) {
        this.db.exec('ALTER TABLE series ADD COLUMN playerId TEXT; ALTER TABLE series ADD COLUMN sessionId TEXT;');
        // Earlier rollups could truncate a weighted mean when every input was an integer.
        // Raw history remains intact and rebuilds these derived rows on the next ingestion.
        this.db.exec("DELETE FROM minute_samples; DELETE FROM history_meta WHERE key='rollup_through';");
      }
      this.db.exec('CREATE INDEX IF NOT EXISTS series_player ON series(playerId,kind,lastMs); PRAGMA user_version=3;');
    })();
    this.playerSessions = new PlayerSessions(this.db);
    this.lastRollup = (this.db.query("SELECT value FROM history_meta WHERE key='rollup_through'").get() as { value: number } | null)?.value ?? 0;
  }

  ingest(snapshot: Snapshot, receivedAtMs = Date.now()) {
    const at = snapshot.clockUtcMs ?? Date.parse(snapshot.capturedAtUtc);
    this.db.transaction(() => this.playerSessions.observe(snapshot, at))();
    if (!snapshot.running || ['menu', 'stopped'].includes(snapshot.role)) return;
    const cutoff = receivedAtMs - this.retentionDays * 86400000;
    const peers = new Map((snapshot.clientTelemetry?.peers ?? []).map(p => [p.peerSessionId, p]));
    const sessions = this.playerSessions.metadata(snapshot);
    const metadata = JSON.stringify({ serverImprovements: improvementSettings(snapshot.serverImprovements), modVersion: snapshot.modVersion, game: snapshot.compatibility ?? null,
      features: snapshot.features?.map(f => ({ id: f.id, enabled: f.enabled })) ?? [],
      scheduler: snapshot.scheduler ? { enabled: snapshot.scheduler.enabled, targetHz: snapshot.scheduler.targetHz, budgetMs: snapshot.scheduler.budgetMs, maxCallsPerFrame: snapshot.scheduler.maxCallsPerFrame, debtCap: snapshot.scheduler.debtCap } : null });
    const source = (kind: HistorySeries['kind'], key: string, peerSessionId: string | null, time: number, name: string, build: string | null,
      playerId: string | null = null, session: string | null = null): HistorySeries => ({
      id: id(snapshot.processSession, snapshot.worldSession, kind, key), processSession: snapshot.processSession,
      worldSession: snapshot.worldSession, peerSessionId, kind, name, modBuildId: build, metadata, firstMs: time, lastMs: time, playerId, sessionId: session,
    });
    const insertSeries = this.db.query(`INSERT INTO series VALUES ($id,$processSession,$worldSession,$peerSessionId,$kind,$name,$modBuildId,$metadata,$firstMs,$lastMs,$playerId,$sessionId)
      ON CONFLICT(id) DO UPDATE SET firstMs=MIN(firstMs,excluded.firstMs),lastMs=MAX(lastMs,excluded.lastMs)`);
    const insert = this.db.query('INSERT OR IGNORE INTO samples VALUES (?,?,?,?,?,?,?,?,?)');
    const rename = this.db.query('UPDATE series SET name=?,modBuildId=COALESCE(?,modBuildId) WHERE id=?');
    const put = (info: HistorySeries, row: Omit<HistoryRow, 'seriesId'>, hasName = false) => {
      if (!Number.isFinite(row.endMs) || row.endMs < cutoff || row.endMs > receivedAtMs + 60000 || row.endMs <= row.startMs) return;
      insertSeries.run({ ...info });
      // A connection may be observed before the optional client handshake supplies its name.
      // Keep the last known name when buffered samples outlive that peer's connection.
      if (hasName) rename.run(info.name, info.modBuildId, info.id);
      insert.run(info.id, row.sequence, row.startMs, row.endMs, row.receivedAtMs, row.clockErrorMs, row.markerMs, row.worstFrameEndMs, JSON.stringify(row.values));
    };
    this.db.transaction(() => {
      const f = snapshot.frameIntervalMs, improved = snapshot.serverImprovements;
      const monoOffset = at - (snapshot.windowEndMonotonicMs ?? 0);
      const common = { sequence: snapshot.sequence, startMs: at - snapshot.sampleWindowSeconds * 1000, endMs: at,
        receivedAtMs, clockErrorMs: 0, markerMs: null, worstFrameEndMs: snapshot.worstFrameEndMonotonicMs == null ? null : snapshot.worstFrameEndMonotonicMs + monoOffset };
      put(source('server', '', null, at, snapshot.role === 'client' ? 'Local game' : 'Server', snapshot.modBuildId ?? null), { ...common, values: packedValues({
        frameMeanMs: f?.mean, frameP95Ms: f?.p95, frameP99Ms: f?.p99, frameMaxMs: f?.max,
        frames: f?.samples ? f.samples : null, fps: f?.samples && snapshot.sampleWindowSeconds > 0 ? f.samples / snapshot.sampleWindowSeconds : null,
        longFrames50: snapshot.longFrames50Ms, longFrames100: snapshot.longFrames100Ms, longFrames250: snapshot.longFrames250Ms,
        gc0: snapshot.gcCollections?.[0], gc1: snapshot.gcCollections?.[1], gc2: snapshot.gcCollections?.[2],
        cpuPercent: snapshot.resources?.cpuPercentOneCore, managedMemoryMiB: snapshot.managedMemoryBytes / 1048576,
        networkP95Ms: snapshot.networkUpdateDurationMs?.p95, changedZdos: snapshot.clientChangedZdos,
        residentMemoryMiB: snapshot.resources?.residentBytes == null ? null : snapshot.resources.residentBytes / 1048576,
        compressionSavedKiB: improved ? (improved.rawPayloadBytes - improved.framedPayloadBytes) / 1024 : null,
        compressedSent: improved?.compressedSent, compressedReceived: improved?.compressedReceived, compressionRejected: improved?.compressionRejected,
        compressionEncodeP95Ms: improved?.compressionEncodeMs?.p95, compressionDecodeP95Ms: improved?.compressionDecodeMs?.p95, captainTransfers: improved?.captainTransfers,
        loadedObjects: snapshot.loadedObjects, worldSaving: snapshot.worldSaving == null ? null : Number(snapshot.worldSaving),
      }) });
      for (const p of snapshot.peers) {
        if (!p.connected) continue;
        const client = peers.get(p.peerSessionId);
        put(source('connection', client?.streamId ?? p.peerSessionId, p.peerSessionId, at, `${client?.name || playerLabel(client?.playerId,p.peerSessionId)} · server connection`, snapshot.modBuildId ?? null,
          client?.playerId ?? null, client ? sessionId(snapshot,client.streamId) : null), {
          ...common, worstFrameEndMs: null, values: packedValues({
            sendWindowKiB: p.improvements == null ? null : p.improvements.windowBytes / 1024,
            steamMaxRateKiBs: p.improvements?.effectiveMaxRateBytesPerSecond == null ? null : p.improvements.effectiveMaxRateBytesPerSecond / 1024,
            steamRateFailures: p.improvements?.rateWriteFailures, rttMs: p.rttMs, queueMs: p.estimatedTransportQueueMs,
            serviceAgeMs: p.replication?.serviceAgeSeconds == null ? null : p.replication.serviceAgeSeconds * 1000 }),
        }, !!client?.name);
      }
      for (const point of snapshot.clientTelemetry?.samples ?? []) {
        const s = point.sample, client = sessions.get(point.streamId);
        const offset = point.endMs - s.endMs;
        const series = source('client', point.streamId, point.peerSessionId, point.endMs, `${client?.name || playerLabel(point.playerId,point.peerSessionId)} · client`, client?.modBuildId ?? null,
          point.playerId ?? client?.playerId ?? null, sessionId(snapshot,point.streamId));
        put(series, { sequence: s.sequence, startMs: point.startMs, endMs: point.endMs, receivedAtMs: point.receivedAtMs,
          clockErrorMs: point.clockErrorMs, markerMs: s.markerMs == null ? null : s.markerMs + offset,
          worstFrameEndMs: s.worstFrameEndMs == null ? null : s.worstFrameEndMs + offset,
          values: packedValues({ ...s, frames: s.frames > 0 ? s.frames : null, fps: s.frames > 0 ? 1000 * s.frames / (s.endMs - s.startMs) : null,
            focused: Number(s.focused), loading: Number(s.loading) } as HistoryValues),
        }, !!client?.name);
      }
    })();
    if (Math.floor(receivedAtMs / 60000) * 60000 > this.lastRollup) this.refreshRollups(receivedAtMs);
    if (receivedAtMs - this.lastCleanup > 60000) this.cleanup(receivedAtMs);
  }

  private refreshRollups(now: number) {
    const through = Math.floor(now / 60000) * 60000;
    const start = Math.max(now - this.retentionDays * 86400000, this.lastRollup - 300000);
    this.db.transaction(() => {
      this.db.query(`INSERT OR REPLACE INTO minute_samples SELECT series_id,CAST(end_ms/60000 AS INTEGER)*60000,
        MIN(start_ms),MAX(end_ms),MAX(received_ms),MAX(clock_error),MIN(marker_ms),CASE WHEN COUNT(*)=1 THEN MIN(worst_ms) END,
        json_array(${historyMetrics.map((_, i) => aggregate(i)).join(',')}),
        json_array(${historyMetrics.map((_, i) => `SUM(${weightSql(i)})`).join(',')}),SUM(end_ms-start_ms),COUNT(*)
        FROM samples WHERE end_ms>=? AND end_ms<? GROUP BY series_id,CAST(end_ms/60000 AS INTEGER)`)
        .run(Math.floor(start / 60000) * 60000, through);
      this.db.query("INSERT OR REPLACE INTO history_meta VALUES ('rollup_through',?)").run(through);
    })();
    this.lastRollup = through;
  }

  // Use stable minute summaries for large ranges, and raw samples at boundaries and for the
  // last five minutes, where delayed client batches may still arrive. Both paths preserve weights.
  private filter(target: string | PlayerSeriesTarget) {
    return typeof target === 'string' ? { sql: 'series_id=?', params: [target] }
      : { sql: 'series_id IN (SELECT id FROM series WHERE playerId=? AND kind=?)', params: [target.playerId,target.kind] };
  }
  private rangeSource(target: string | PlayerSeriesTarget, from: number, to: number, stepMs: number, now: number) {
    const filter = this.filter(target);
    const raw = `SELECT series_id,start_ms,end_ms,received_ms,clock_error,marker_ms,worst_ms,v,
      json_array(${historyMetrics.map((_, i) => weightSql(i)).join(',')}) AS w,end_ms-start_ms AS observed_ms,1 AS n FROM samples`;
    const left = Math.ceil(from / 60000) * 60000;
    const right = Math.min(Math.floor(to / 60000) * 60000, this.lastRollup - 300000, Math.floor(now / 60000) * 60000 - 300000);
    if (stepMs < 60000 || right <= left) return { sql: `${raw} WHERE ${filter.sql} AND end_ms>=? AND end_ms<=?`, params: [...filter.params, from, to] };
    return { sql: `SELECT series_id,start_ms,end_ms,received_ms,clock_error,marker_ms,worst_ms,v,w,observed_ms,n FROM minute_samples WHERE ${filter.sql} AND bucket>=? AND bucket<?
      UNION ALL ${raw} WHERE ${filter.sql} AND end_ms>=? AND end_ms<=? AND (end_ms<? OR end_ms>=?)`, params: [...filter.params, left, right, ...filter.params, from, to, left, right] };
  }

  cleanup(now = Date.now()) {
    const cutoff = now - this.retentionDays * 86400000;
    this.db.transaction(() => {
      this.db.query('DELETE FROM samples WHERE end_ms < ?').run(cutoff);
      this.db.query('DELETE FROM minute_samples WHERE bucket < ?').run(Math.floor(cutoff / 60000) * 60000);
      this.db.query('DELETE FROM series WHERE lastMs < ?').run(cutoff);
      this.playerSessions.cleanup(cutoff);
    })();
    this.db.exec('PRAGMA wal_checkpoint(PASSIVE)');
    this.lastCleanup = now;
  }

  private groupedSeries(from: number,to: number,target?: PlayerSeriesTarget): HistorySeries[] {
    const rows = this.db.query(`SELECT playerId,kind,MIN(firstMs) AS firstMs,MAX(lastMs) AS lastMs,COUNT(DISTINCT sessionId) AS sessionCount,
      (SELECT name FROM player_sessions p WHERE p.playerId=s.playerId ORDER BY p.lastSeenMs DESC LIMIT 1) AS name
      FROM series s WHERE playerId IS NOT NULL AND lastMs>=? AND firstMs<=? ${target ? 'AND playerId=? AND kind=?' : ''}
      GROUP BY playerId,kind ORDER BY lastMs DESC LIMIT 257`).all(from,to,...(target ? [target.playerId,target.kind] : [])) as { playerId:string; kind:'client'|'connection'; firstMs:number; lastMs:number; sessionCount:number; name:string|null }[];
    return rows.map(r => ({ ...r,id:id('player',r.playerId,r.kind),name:`${r.name || playerLabel(r.playerId,'')} · ${r.kind === 'client' ? 'client' : 'server connection'}`,
      processSession:'multiple',worldSession:null,peerSessionId:null,sessionId:null,modBuildId:null,metadata:'{"groupedBy":"player"}',grouped:true }));
  }
  series(fromMs: number, toMs: number, now = Date.now(),groupPlayers = false): HistorySeries[] {
    const from = Math.max(fromMs, now - this.retentionDays * 86400000);
    const single = this.db.query(`SELECT * FROM series WHERE lastMs >= ? AND firstMs <= ? ${groupPlayers ? 'AND playerId IS NULL' : ''} ORDER BY lastMs DESC,kind,id LIMIT 257`)
      .all(from, toMs) as HistorySeries[];
    return (groupPlayers ? [...single,...this.groupedSeries(from,toMs)] : single).sort((a,b)=>b.lastMs-a.lastMs || a.id.localeCompare(b.id)).slice(0,257);
  }
  sessions(fromMs:number,toMs:number,playerIds?:string[],now=Date.now()) {
    return this.playerSessions.list(Math.max(fromMs,now-this.retentionDays*86400000),toMs,playerIds);
  }

  query(q: HistoryQuery, now = Date.now()): HistoryResult {
    const from = Math.max(q.fromMs, now - this.retentionDays * 86400000);
    const index = historyMetrics.findIndex(m => m.key === q.metric);
    if (index < 0 || q.seriesIds.length + (q.playerSeries?.length ?? 0) > 9 || !Number.isInteger(q.points) || q.points < 10 || q.points > 1200
      || !Number.isFinite(q.fromMs) || !Number.isFinite(q.toMs) || q.toMs <= q.fromMs || q.toMs - q.fromMs > 366 * 86400000) throw new Error('Invalid history query');
    let stepMs = Math.max(1000, Math.ceil((q.toMs - from) / q.points / 1000) * 1000);
    if (stepMs >= 60000) stepMs = Math.ceil(stepMs / 60000) * 60000;
    const result: HistoryResult = { fromMs: q.fromMs, retainedFromMs: from, toMs: q.toMs, stepMs, metric: q.metric, retentionDays: this.retentionDays, series: [], markers: [], markersTruncated: false };
    const targets: (string | PlayerSeriesTarget)[] = [...new Set(q.seriesIds),...new Map((q.playerSeries ?? []).map(p=>[`${p.playerId}:${p.kind}`,p])).values()];
    for (const target of targets) {
      const info = typeof target === 'string' ? this.db.query('SELECT * FROM series WHERE id=?').get(target) as HistorySeries | null : this.groupedSeries(from,q.toMs,target)[0];
      if (!info) continue;
      const input = this.rangeSource(target, from, q.toMs, stepMs, now);
      const points = this.db.query(`SELECT CAST(end_ms / ? AS INTEGER) * ? AS at, ${rolledAggregate(index)} AS value,
        SUM(n) AS samples,SUM(observed_ms) AS observedMs,MAX(clock_error) AS clockErrorMs FROM (${input.sql})
        GROUP BY at ORDER BY at LIMIT 1202`).all(stepMs, stepMs, ...input.params) as HistoryResult['series'][number]['points'];
      const summary = this.db.query(`SELECT ${rolledAggregate(index)} AS value,COALESCE(SUM(n),0) AS samples,COALESCE(SUM(observed_ms),0) AS observedMs,MAX(clock_error) AS clockErrorMs FROM (${input.sql})`)
        .get(...input.params) as HistorySummary;
      result.series.push({ info, points,summary });
      const filter = this.filter(target);
      const markers = this.db.query(`SELECT series_id AS seriesId,marker_ms AS at,clock_error AS clockErrorMs FROM samples
        WHERE ${filter.sql} AND end_ms>=? AND end_ms<=? AND marker_ms>=? AND marker_ms<=? ORDER BY marker_ms LIMIT 201`).all(...filter.params, from, q.toMs + 120000, from, q.toMs) as HistoryResult['markers'];
      result.markers.push(...markers.map(m=>({...m,seriesId:info.id})));
    }
    result.markers.sort((a, b) => a.at - b.at);
    result.markersTruncated = result.markers.length > 200;
    result.markers = result.markers.slice(0, 200);
    return result;
  }

  archive(fromMs: number, toMs: number, processSession: string, worldSession: string | null, now = Date.now()): HistoryArchive {
    const from = Math.max(fromMs, now - this.retentionDays * 86400000);
    const all = this.db.query("SELECT * FROM series WHERE processSession=? AND worldSession IS ? AND lastMs>=? AND firstMs<=? ORDER BY CASE kind WHEN 'server' THEN 0 WHEN 'client' THEN 1 ELSE 2 END,lastMs DESC,id LIMIT 65")
      .all(processSession, worldSession, from, toMs) as HistorySeries[];
    const series = all.slice(0, 64);
    let stepMs = Math.max(1000, Math.ceil((toMs - from) / Math.max(1, Math.floor(2400 / Math.max(1, series.length))) / 1000) * 1000);
    if (stepMs >= 60000) stepMs = Math.ceil(stepMs / 60000) * 60000;
    const rows: HistoryRow[] = [];
    for (const s of series) {
      const input = this.rangeSource(s.id, from, toMs, stepMs, now);
      const records = this.db.query(`SELECT MIN(start_ms) AS startMs,MAX(end_ms) AS endMs,MAX(received_ms) AS receivedAtMs,
        MAX(clock_error) AS clockErrorMs,MIN(marker_ms) AS markerMs,CASE WHEN SUM(n)=1 THEN MIN(worst_ms) END AS worstFrameEndMs,
        SUM(observed_ms) AS observedMs,SUM(n) AS samples,
        json_array(${historyMetrics.map((_, i) => `COALESCE(SUM(json_extract(w,'$[${i}]')),0)`).join(',')}) AS weights,
        ${historyMetrics.map((_, i) => `${rolledAggregate(i)} AS v${i}`).join(',')}
        FROM (${input.sql}) GROUP BY CAST(end_ms / ? AS INTEGER) ORDER BY endMs LIMIT 2402`)
        .all(...input.params, stepMs) as (Record<string, number | null> & { weights: string })[];
      let seq = 0;
      for (const r of records) rows.push({ seriesId: s.id, sequence: ++seq, startMs: r.startMs!, endMs: r.endMs!,
        receivedAtMs: r.receivedAtMs!, clockErrorMs: r.clockErrorMs, markerMs: r.markerMs, worstFrameEndMs: r.worstFrameEndMs,
        observedMs: r.observedMs!, samples: r.samples!,
        weights: JSON.parse(r.weights),
        values: historyMetrics.map((_, i) => r[`v${i}`]) });
    }
    const sessionIds = new Set(series.map(s=>s.sessionId).filter(Boolean));
    const sessions = [...sessionIds].map(s=>this.db.query('SELECT * FROM player_sessions WHERE id=?').get(s!)).filter(Boolean) as NonNullable<HistoryArchive['sessions']>;
    return { version: 1, metricLayout: 2, fromMs, toMs, retainedFromMs: from, stepMs, series, rows, sessions,seriesTruncated: all.length > 64 };
  }

  status(now = Date.now()): HistoryStatus {
    const cutoff = now - this.retentionDays * 86400000;
    const first = this.db.query('SELECT end_ms AS at FROM samples WHERE end_ms>=? ORDER BY end_ms LIMIT 1').get(cutoff) as { at: number } | null;
    const last = this.db.query('SELECT end_ms AS at FROM samples WHERE end_ms>=? ORDER BY end_ms DESC LIMIT 1').get(cutoff) as { at: number } | null;
    const pages = (this.db.query('PRAGMA page_count').get() as { page_count: number }).page_count;
    const size = (this.db.query('PRAGMA page_size').get() as { page_size: number }).page_size;
    return { earliestMs: first?.at ?? null, latestMs: last?.at ?? null, retentionDays: this.retentionDays, databaseBytes: pages * size, error: null, droppedOffers: 0 };
  }
  close() { this.db.close(); }
}
