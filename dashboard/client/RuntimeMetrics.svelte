<script lang="ts">
  import type { Snapshot, HistoryPoint } from '../shared/telemetry';
  import MetricChart from './MetricChart.svelte';
  let { snapshot, history }: { snapshot: Snapshot; history: HistoryPoint[] } = $props();
  const n = (value: number | null | undefined, digits = 1) => value == null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: digits });
  const cpu = $derived([{ name: 'Process CPU · one core = 100%', data: history.map(p => [p.at, p.cpuPercentOneCore ?? null] as [number, number | null]) }]);
  const age = $derived([{ name: 'Oldest observed send opportunity', data: history.map(p => [p.at, p.maxServiceAgeMs ?? null] as [number, number | null]) }]);
</script>

<section class="panel" id="runtime">
  <div class="panel-heading"><div><h2>Server resources &amp; scheduling</h2><p class="muted">Counters cover the last {n(snapshot.sampleWindowSeconds, 2)} seconds. CPU measures this Valheim process; 100% is one occupied core.</p></div></div>
  <div class="detail-grid">
    <div><span>Process CPU</span><strong>{n(snapshot.resources?.cpuPercentOneCore)}%</strong></div>
    <div><span>Resident memory</span><strong>{n(snapshot.resources?.residentBytes == null ? null : snapshot.resources.residentBytes / 1048576)} MiB</strong></div>
    <div><span>Process threads</span><strong>{n(snapshot.resources?.threads, 0)}</strong></div>
    <div><span>Frame interval p99</span><strong>{n(snapshot.frameIntervalMs?.p99)} ms</strong></div>
    <div><span>Frames ≥50 / ≥100 ms</span><strong>{n(snapshot.longFrames50Ms, 0)} / {n(snapshot.longFrames100Ms, 0)}</strong></div>
    <div><span>World save</span><strong>{snapshot.worldSaving == null ? '—' : snapshot.worldSaving ? 'In progress' : 'Idle'}</strong></div>
    <div><span>Last save · reported elapsed</span><strong>{n(snapshot.lastSaveDurationMs)} ms</strong></div>
    <div><span>Save preparation</span><strong>{n(snapshot.lastSavePreparationMs)} ms</strong></div>
  </div>
  <p class="runtime-note muted">Resources: {snapshot.resources?.status ?? 'not reported'}. Save elapsed includes the game's completion-notification delay; it is not disk-write time.</p>
  {#if snapshot.scheduler}
    <div class="panel-heading"><div><h2>Fair scheduler · {snapshot.scheduler.status}</h2><p class="muted">{snapshot.scheduler.enabled ? 'Experimental scheduler configured on' : 'Vanilla scheduling configured'} · {snapshot.scheduler.targetHz} Hz target · {snapshot.scheduler.maxCallsPerFrame} calls/frame · {snapshot.scheduler.budgetMs} ms soft budget · debt cap {snapshot.scheduler.debtCap}/peer</p></div></div>
    <div class="detail-grid">
      <div><span>Scheduled calls</span><strong>{n(snapshot.scheduler.calls, 0)}</strong></div>
      <div><span>Pending service credit</span><strong>{n(snapshot.scheduler.pendingDebt)}</strong></div>
      <div><span>Time / work limited frames</span><strong>{n(snapshot.scheduler.timeLimitedFrames, 0)} / {n(snapshot.scheduler.workLimitedFrames, 0)}</strong></div>
      <div><span>Scheduler work p95 / max</span><strong>{n(snapshot.scheduler.frameWorkMs?.p95)} / {n(snapshot.scheduler.frameWorkMs?.max)} ms</strong></div>
    </div>
    <p class="runtime-note muted">Eligible peers: {snapshot.scheduler.eligiblePeers}. Discarded catch-up credit: {n(snapshot.scheduler.discardedDebt)}. A single send cannot be interrupted and may exceed the time budget. Service credit is not queued network data.</p>
  {/if}
</section>
<div class="charts"><MetricChart title="Valheim process CPU" unit="%" series={cpu} /><MetricChart title="Replication service age" unit="ms" series={age} /></div>
<section class="panel" id="replication">
  <div class="panel-heading"><div><h2>Replication &amp; heartbeat</h2><p class="muted">These measurements do not depend on a successful native Steam status query. Send opportunities are not delivery acknowledgments.</p></div></div>
  <div class="table-scroll"><table><thead><tr><th>Peer session</th><th>Heartbeat age</th><th>Service age</th><th>Service interval p95</th><th>Send work p95</th><th>Attempts / batches</th><th>Empty or deferred / errors</th><th>ZDOs sent</th><th>Payload received</th><th>Queued packets / bytes</th><th>Queue growth</th><th>Queue nonempty</th><th>Health probe</th></tr></thead><tbody>
    {#each snapshot.peers as peer (peer.peerSessionId)}
      <tr><td>{peer.peerSessionId}</td><td>{n(peer.heartbeatAgeSeconds)} s</td><td>{n(peer.replication?.serviceAgeSeconds)} s</td><td>{n(peer.replication?.serviceIntervalMs?.p95)} ms</td><td>{n(peer.replication?.sendDurationMs?.p95)} ms</td><td>{n(peer.replication?.sendAttempts, 0)} / {n(peer.replication?.sentBatches, 0)}</td><td>{n(peer.replication?.noDataOrDeferred, 0)} / {n(peer.replication?.sendFailures, 0)}</td><td>{n(peer.replication?.sentZdos, 0)}</td><td>{n(peer.replication?.receivedPayloadBytes == null ? null : peer.replication.receivedPayloadBytes / 1024)} KiB</td><td>{n(peer.applicationQueuedPackets, 0)} / {n(peer.applicationQueuedBytes, 0)}</td><td>{n(peer.applicationQueueGrowthBytesPerSecond)} B/s</td><td>{n(peer.applicationQueueNonemptySeconds)} s</td><td>{peer.connectionHealthStatus ?? 'not reported'}</td></tr>
    {:else}<tr><td colspan="13" class="empty">Awaiting a ready peer.</td></tr>{/each}
  </tbody></table></div>
  <p class="runtime-note muted">Heartbeat age is the game's elapsed time since a reply, not RTT. A false send result combines no relevant changes with queue deferral. Queue nonempty duration is sampled continuously nonempty time, not the age of an individual packet.</p>
</section>
