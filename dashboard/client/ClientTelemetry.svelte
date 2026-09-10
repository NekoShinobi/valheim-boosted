<script lang="ts">
  import type { Snapshot } from '../shared/telemetry';
  let { snapshot }: { snapshot: Snapshot } = $props();
  const telemetry = $derived(snapshot.clientTelemetry);
  const latest = $derived.by(() => {
    const points = new Map<string, NonNullable<Snapshot['clientTelemetry']>['samples'][number]>();
    for (const point of telemetry?.samples ?? []) if (point.sample.sequence > (points.get(point.streamId)?.sample.sequence ?? 0)) points.set(point.streamId, point);
    return points;
  });
  const n = (v: number | null | undefined) => v == null ? '—' : v.toLocaleString(undefined, { maximumFractionDigits: 1 });
</script>
<section class="panel" id="client-telemetry">
  <div class="panel-heading"><div><h2>Player performance</h2><p class="muted">Reported by connected clients.</p></div><a href="/history" class="outline">View history ↗</a></div>
  {#if telemetry}
    {#if telemetry.identityStatus === 'identity_key_invalid'}<p class="runtime-note">Returning-player grouping is unavailable: the server's ClientTelemetry.IdentityKey is invalid. Restore its original 64-character hexadecimal key and restart the server.</p>{/if}
    <details class="measurement-notes"><summary>Telemetry delivery</summary><p>{telemetry.status} · {n(telemetry.receivedBytes / 1024)} KiB received · {telemetry.rejectedMessages} rejected messages · {telemetry.congestionSkips} sends deferred · {telemetry.droppedSamples} samples dropped</p></details>
    <div class="table-scroll"><table><thead><tr><th>Player / session</th><th>Reporting</th><th>FPS</th><th>Frame p95</th><th>Sample age</th><th>Clock uncertainty</th></tr></thead><tbody>
      {#each telemetry.peers as peer (peer.streamId)}
        {@const point = latest.get(peer.streamId)}
        {@const age = point ? Math.max(0, (Date.now() - point.receivedAtMs) / 1000) : null}
        <tr><td><a href={peer.playerId?`/history?player=${peer.playerId}`:`/history?peer=${encodeURIComponent(peer.peerSessionId)}`}>{peer.name || (peer.playerId?`Player ${peer.playerId.slice(-6)}`:peer.peerSessionId)} ↗</a></td><td>{age != null && age > 10 ? 'stale' : peer.status}</td><td>{n(point && point.sample.frames > 0 ? point.sample.frames * 1000 / (point.sample.endMs - point.sample.startMs) : null)}</td><td>{n(point?.sample.frameP95Ms)} ms</td><td>{n(age)} s</td><td>±{n(peer.clockErrorMs)} ms</td></tr>
      {:else}<tr><td colspan="6" class="empty">No ready peers. Clients need valheim-boosted with sharing enabled to report performance.</td></tr>{/each}
    </tbody></table></div>
  {:else}<p class="runtime-note muted">This mod build does not export client telemetry. Upgrade the server and participating clients to collect player performance.</p>{/if}
</section>
