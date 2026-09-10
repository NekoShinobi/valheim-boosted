export interface ClientWindow {
  sequence: number; startMs: number; endMs: number; frames: number;
  frameMeanMs: number | null; frameP95Ms: number | null; frameP99Ms: number | null; frameMaxMs: number | null; worstFrameEndMs: number | null;
  longFrames50: number | null; longFrames100: number | null; longFrames250: number | null;
  gc0: number; gc1: number; gc2: number; cpuPercent: number | null; managedMemoryMiB: number | null;
  networkP95Ms: number | null; rttMs: number | null; queueMs: number | null; changedZdos: number | null;
  focused: boolean; loading: boolean; frameLimit: number; vSyncCount: number; markerMs: number | null;
}
export interface ClientSession {
  streamId: string; peerSessionId: string; playerId?: string | null; name: string | null; modBuildId: string | null;
  status: string; clockErrorMs: number | null; lastReceivedAtMs: number | null;
  sessionStartedAtMs?: number; sessionEndedAtMs?: number | null; lastSeenAtMs?: number;
}
export interface ClientTelemetry {
  protocolVersion: 1; receiveEnabled: boolean; shareEnabled: boolean; status: string;
  sentBytes: number; receivedBytes: number; rejectedMessages: number; congestionSkips: number; droppedSamples: number;
  identityStatus?: string;
  peers: ClientSession[];
  sessions?: ClientSession[];
  samples: { streamId: string; peerSessionId: string; playerId?: string | null; startMs: number; endMs: number; receivedAtMs: number; clockErrorMs: number; sample: ClientWindow }[];
}
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const num = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= Number.MAX_SAFE_INTEGER;
const nullable = (v: unknown) => v === null || num(v);
const str = (v: unknown) => typeof v === 'string' && v.length <= 128;
export const validPlayerId = (v: unknown): v is string => typeof v === 'string' && /^[a-f0-9]{64}$/.test(v);
const session = (p: unknown) => object(p) && ['streamId', 'peerSessionId', 'status'].every(k => str(p[k]))
  && (p.playerId == null || validPlayerId(p.playerId))
  && ['name', 'modBuildId'].every(k => p[k] === null || str(p[k])) && nullable(p.clockErrorMs) && nullable(p.lastReceivedAtMs)
  && ['sessionStartedAtMs', 'lastSeenAtMs', 'sessionEndedAtMs'].every(k => p[k] == null || num(p[k]))
  && (p.sessionStartedAtMs == null || num(p.lastSeenAtMs) && p.lastSeenAtMs >= Number(p.sessionStartedAtMs))
  && (p.sessionEndedAtMs == null || num(p.sessionStartedAtMs) && num(p.lastSeenAtMs) && Number(p.sessionEndedAtMs) >= p.lastSeenAtMs);
export function validClientTelemetry(v: unknown): v is ClientTelemetry {
  if (!object(v) || v.protocolVersion !== 1 || typeof v.receiveEnabled !== 'boolean' || typeof v.shareEnabled !== 'boolean' || !str(v.status)
    || !['sentBytes', 'receivedBytes', 'rejectedMessages', 'congestionSkips', 'droppedSamples'].every(k => num(v[k]))
    || !Array.isArray(v.peers) || v.peers.length > 64 || !Array.isArray(v.samples) || v.samples.length > 512) return false;
  if (!v.peers.every(session) || v.identityStatus != null && !str(v.identityStatus)) return false;
  if (v.sessions != null && (!Array.isArray(v.sessions) || v.sessions.length > 192 || !v.sessions.every(session)
    || new Set(v.sessions.map(p => p.streamId)).size !== v.sessions.length)) return false;
  if (new Set(v.peers.map(p => p.streamId)).size !== v.peers.length) return false;
  return v.samples.every(p => {
    if (!object(p) || !str(p.streamId) || !str(p.peerSessionId) || p.playerId != null && !validPlayerId(p.playerId) || !['startMs', 'endMs', 'receivedAtMs', 'clockErrorMs'].every(k => num(p[k]))
      || Number(p.endMs) <= Number(p.startMs) || Number(p.endMs) - Number(p.startMs) > 120001 || !object(p.sample)) return false;
    const s = p.sample;
    return Number.isSafeInteger(s.sequence) && Number(s.sequence) > 0 && ['startMs', 'endMs', 'frames', 'gc0', 'gc1', 'gc2', 'vSyncCount'].every(k => num(s[k]))
      && Number(s.endMs) > Number(s.startMs) && Number(s.endMs) - Number(s.startMs) <= 120001
      && Math.abs((Number(p.endMs) - Number(p.startMs)) - (Number(s.endMs) - Number(s.startMs))) < 1
      && ['frames', 'gc0', 'gc1', 'gc2', 'vSyncCount'].every(k => Number.isSafeInteger(s[k])) && Number(s.frames) <= 1000000 && Number(s.vSyncCount) <= 4
      && ['frameMeanMs', 'frameP95Ms', 'frameP99Ms', 'frameMaxMs', 'worstFrameEndMs', 'longFrames50', 'longFrames100', 'longFrames250', 'cpuPercent', 'managedMemoryMiB', 'networkP95Ms', 'rttMs', 'queueMs', 'changedZdos', 'markerMs'].every(k => nullable(s[k]))
      && ['markerMs', 'worstFrameEndMs'].every(k => s[k] === null || Number(s[k]) >= Number(s.startMs) && Number(s[k]) <= Number(s.endMs))
      && typeof s.focused === 'boolean' && typeof s.loading === 'boolean' && typeof s.frameLimit === 'number' && Number.isInteger(s.frameLimit) && s.frameLimit >= -1 && s.frameLimit <= 10000;
  });
}
