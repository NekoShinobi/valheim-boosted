import { snapshot, peer } from './fixtures';
import type { Snapshot } from '../shared/telemetry';

export function historySnapshot(sequence = 1, now = Date.now()): Snapshot {
  const s = snapshot(sequence, now);
  const end = 100000 + sequence * 1000;
  s.clockUtcMs = now; s.windowEndMonotonicMs = end; s.worstFrameEndMonotonicMs = end - 100;
  s.modBuildId = 'server-fixture'; s.gcCollections = [1, 0, 0]; s.longFrames250Ms = 0;
  s.peers = [peer()];
  s.clientTelemetry = {
    protocolVersion: 1, receiveEnabled: true, shareEnabled: true, status: 'receiving',
    sentBytes: 100, receivedBytes: 300, rejectedMessages: 0, congestionSkips: 0, droppedSamples: 0,
    peers: [{ streamId: 'test-client-stream', peerSessionId: peer().peerSessionId, name: 'Player One', modBuildId: 'client-fixture', status: 'reporting', clockErrorMs: 20, lastReceivedAtMs: now }],
    samples: [{ streamId: 'test-client-stream', peerSessionId: peer().peerSessionId, startMs: now - 5000, endMs: now - 4000, receivedAtMs: now, clockErrorMs: 20,
      sample: { sequence, startMs: end - 1000, endMs: end, frames: 20, frameMeanMs: 50, frameP95Ms: 55, frameP99Ms: 80, frameMaxMs: 90, worstFrameEndMs: end - 200,
        longFrames50: 4, longFrames100: 0, longFrames250: 0, gc0: 1, gc1: 0, gc2: 0, cpuPercent: 135, managedMemoryMiB: 512,
        networkP95Ms: 5, rttMs: 60, queueMs: 1, changedZdos: 15, focused: true, loading: false, frameLimit: -1, vSyncCount: 0, markerMs: end - 100 } }],
  };
  return s;
}
