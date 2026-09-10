import type { Database } from 'bun:sqlite';
import { createHash } from 'node:crypto';
import type { Snapshot } from '../shared/telemetry';
import type { ClientSession } from '../shared/client-telemetry';
import type { PlayerSession } from '../shared/history';

export const sessionId = (s: Snapshot, stream: string) => createHash('sha256').update(JSON.stringify([s.processSession, s.worldSession, 'session', stream])).digest('hex').slice(0, 32);
export const playerLabel = (playerId: string | null | undefined, peer: string) => playerId ? `Player ${playerId.slice(-6)}` : peer;

export class PlayerSessions {
  constructor(private readonly db: Database) {
    db.exec(`CREATE TABLE IF NOT EXISTS player_sessions (
      id TEXT PRIMARY KEY,playerId TEXT,streamId TEXT NOT NULL,processSession TEXT NOT NULL,worldSession TEXT,peerSessionId TEXT NOT NULL,
      name TEXT NOT NULL,startedMs REAL NOT NULL,lastSeenMs REAL NOT NULL,endedMs REAL,endReason TEXT
    ); CREATE INDEX IF NOT EXISTS player_session_identity ON player_sessions(playerId,lastSeenMs);
    CREATE INDEX IF NOT EXISTS player_session_time ON player_sessions(lastSeenMs);`);
  }
  observe(s: Snapshot, at: number) {
    // A new server/world establishes that older observed connections cannot still be current.
    this.db.query("UPDATE player_sessions SET endedMs=lastSeenMs,endReason='server_changed' WHERE endedMs IS NULL AND (processSession<>? OR worldSession IS NOT ?)")
      .run(s.processSession, s.worldSession);
    if (!s.running || ['stopped', 'menu'].includes(s.role)) {
      this.db.query("UPDATE player_sessions SET endedMs=MAX(lastSeenMs,?),endReason='server_stopped' WHERE endedMs IS NULL AND processSession=?")
        .run(at, s.processSession);
      return;
    }
    if (!s.clientTelemetry) return;
    const sessions = s.clientTelemetry.sessions ?? s.clientTelemetry.peers;
    const upsert = this.db.query(`INSERT INTO player_sessions VALUES (?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET
      playerId=COALESCE(player_sessions.playerId,excluded.playerId),
      name=CASE WHEN excluded.lastSeenMs>=lastSeenMs THEN excluded.name ELSE name END,
      startedMs=MIN(startedMs,excluded.startedMs),lastSeenMs=MAX(lastSeenMs,excluded.lastSeenMs),
      endedMs=CASE WHEN excluded.endedMs IS NOT NULL THEN excluded.endedMs WHEN endReason='not_observed' THEN NULL ELSE endedMs END,
      endReason=CASE WHEN excluded.endedMs IS NOT NULL THEN excluded.endReason WHEN endReason='not_observed' THEN NULL ELSE endReason END`);
    for (const p of sessions) {
      const last = Math.min(p.lastSeenAtMs ?? at, at);
      const start = Math.min(p.sessionStartedAtMs ?? last, last);
      const end = p.sessionEndedAtMs == null ? null : Math.max(last, Math.min(p.sessionEndedAtMs, at));
      upsert.run(sessionId(s,p.streamId),p.playerId ?? null,p.streamId,s.processSession,s.worldSession,p.peerSessionId,
        p.name || playerLabel(p.playerId,p.peerSessionId),start,last,end,end == null ? null : 'disconnect');
    }
    const open = this.db.query('SELECT id FROM player_sessions WHERE endedMs IS NULL AND processSession=? AND worldSession IS ?')
      .all(s.processSession,s.worldSession) as { id: string }[];
    const present = new Set(sessions.map(p => sessionId(s,p.streamId)));
    for (const p of open) if (!present.has(p.id)) this.db.query("UPDATE player_sessions SET endedMs=lastSeenMs,endReason='not_observed' WHERE id=?").run(p.id);
  }
  metadata(s: Snapshot): Map<string, ClientSession> {
    return new Map((s.clientTelemetry?.sessions ?? s.clientTelemetry?.peers ?? []).map(p => [p.streamId,p]));
  }
  list(from: number,to: number,playerIds?: string[]): PlayerSession[] {
    if (playerIds && !playerIds.length) return [];
    return this.db.query(`SELECT * FROM player_sessions WHERE startedMs<=? AND COALESCE(endedMs,lastSeenMs)>=?
      ${playerIds ? `AND playerId IN (${playerIds.map(() => '?').join(',')})` : ''} ORDER BY startedMs DESC,id LIMIT 257`)
      .all(to,from,...(playerIds ?? [])) as PlayerSession[];
  }
  cleanup(cutoff: number) { this.db.query('DELETE FROM player_sessions WHERE COALESCE(endedMs,lastSeenMs)<?').run(cutoff); }
}
