import type { Timing } from './telemetry';

export interface ImprovementSettings {
  windowsEnabled: boolean; rateEnabled: boolean; captainEnabled: boolean; compressionEnabled: boolean;
  targetBytesPerSecond: number; maximumWindowBytes: number; requestedMaxRateBytesPerSecond: number; compressionBudgetMs: number;
}
export interface ServerImprovements extends ImprovementSettings {
  steamConfigInterface: string | null; captainStatus: string;
  captainCandidates: number; captainTransfers: number; captainDeferred: number;
  compressionEncodeMs: Timing | null; compressionDecodeMs: Timing | null;
  rawPayloadBytes: number; framedPayloadBytes: number; compressedSent: number; compressedReceived: number;
  compressionSkipped: number; compressionRejected: number;
}
export interface PeerImprovements {
  windowBytes: number; windowStatus: string | null; rateStatus: string | null; compressionStatus: string | null;
  originalMaxRateBytesPerSecond: number | null; effectiveMaxRateBytesPerSecond: number | null; minimumRateBytesPerSecond: number | null;
  rateWriteFailures: number;
}
const object = (x: unknown): x is Record<string, unknown> => !!x && typeof x === 'object' && !Array.isArray(x);
const num = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x) && x >= 0;
const text = (x: unknown) => typeof x === 'string' && x.length <= 256;
const within = (x: unknown, min: number, max: number) => num(x) && x >= min && x <= max;
export const improvementSettings = (s: ServerImprovements | null | undefined): ImprovementSettings | null => s ? {
  windowsEnabled: s.windowsEnabled, rateEnabled: s.rateEnabled, captainEnabled: s.captainEnabled, compressionEnabled: s.compressionEnabled,
  targetBytesPerSecond: s.targetBytesPerSecond, maximumWindowBytes: s.maximumWindowBytes,
  requestedMaxRateBytesPerSecond: s.requestedMaxRateBytesPerSecond, compressionBudgetMs: s.compressionBudgetMs,
} : null;
export function validImprovementSettings(s: unknown): s is ImprovementSettings {
  return object(s) && ['windowsEnabled', 'rateEnabled', 'captainEnabled', 'compressionEnabled'].every(k => typeof s[k] === 'boolean')
    && within(s.targetBytesPerSecond, 10240, 1048576) && within(s.maximumWindowBytes, 10240, 65536)
    && within(s.requestedMaxRateBytesPerSecond, 10240, 1048576) && within(s.compressionBudgetMs, 0.1, 5);
}
export function validImprovements(s: unknown, timing: (x: unknown) => boolean): s is ServerImprovements {
  return object(s) && validImprovementSettings(s) && (s.steamConfigInterface === null || text(s.steamConfigInterface)) && text(s.captainStatus)
    && ['captainCandidates', 'captainTransfers', 'captainDeferred', 'rawPayloadBytes', 'framedPayloadBytes', 'compressedSent', 'compressedReceived', 'compressionSkipped', 'compressionRejected'].every(k => num(s[k]))
    && Number(s.framedPayloadBytes) <= Number(s.rawPayloadBytes) && timing(s.compressionEncodeMs) && timing(s.compressionDecodeMs);
}
export function validPeerImprovements(p: unknown): p is PeerImprovements {
  return object(p) && within(p.windowBytes, 10240, 65536) && num(p.rateWriteFailures)
    && ['windowStatus', 'rateStatus', 'compressionStatus'].every(k => p[k] === null || text(p[k]))
    && ['originalMaxRateBytesPerSecond', 'effectiveMaxRateBytesPerSecond', 'minimumRateBytesPerSecond'].every(k => p[k] === null || num(p[k]));
}
