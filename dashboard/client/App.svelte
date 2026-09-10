<script lang="ts">
  import { onMount } from 'svelte';
  import MetricChart from './MetricChart.svelte';
  import ConnectionDiagnostics from './ConnectionDiagnostics.svelte';
  import RuntimeMetrics from './RuntimeMetrics.svelte';
  import ReportControls from './ReportControls.svelte';
  import ClientTelemetry from './ClientTelemetry.svelte';
  import ServerImprovements from './ServerImprovements.svelte';
  import type { MetricsResponse } from '../shared/telemetry';

  let data = $state<MetricsResponse | null>(null);
  let error = $state(false);
  let selected = $state('');
  let metric = $state<'rttMs' | 'estimatedTransportQueueMs'>('rttMs');
  let revision = 0;
  const snapshot = $derived(data?.snapshot);
  const peers = $derived(snapshot?.peers ?? []);
  const active = $derived(peers.find(p => p.peerSessionId === selected));
  const unavailablePeers = $derived(peers.filter(p => p.connected && p.measurementStatus !== 'available'));
  const status = $derived(error ? 'offline' : data?.status ?? 'waiting');
  const improvementsActive = $derived(snapshot?.features?.some(f => ['SendWindows', 'SteamRate', 'CaptainOwnership', 'Compression'].includes(f.id) && f.status === 'active'));
  const blockedProbes = $derived(snapshot?.features?.filter(f => f.enabled && !['available', 'active', 'installed_waiting'].includes(f.status)).length ?? 0);
  const short = (id: string) => id.length > 8 ? '…' + id.slice(-8) : id;
  const number = (value: number | null | undefined, digits = 1) => value == null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: digits });
  const kib = (value: number | null | undefined) => value == null ? '—' : number(value / 1024);
  const connections = $derived((selected ? peers.filter(p => p.peerSessionId === selected) : peers.slice(0, 8)).map(p => ({
    name: short(p.peerSessionId),
    data: (data?.history ?? []).map(point => [point.at, point.peers.find(x => x.peerSessionId === p.peerSessionId)?.[metric] ?? null] as [number, number | null]),
  })));
  const timing = $derived([
    { name: 'Frame interval p95', data: (data?.history ?? []).map(p => [p.at, p.frameP95] as [number, number | null]) },
    { name: 'ZDO update p95', data: (data?.history ?? []).map(p => [p.at, p.networkP95] as [number, number | null]) },
  ]);
  $effect(() => { if (selected && !peers.some(p => p.peerSessionId === selected)) selected = ''; });

  onMount(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    let controller: AbortController;
    async function poll() {
      const startedRevision = revision;
      controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 5000);
      try {
        const response = await fetch('/api/metrics', { cache: 'no-store', signal: controller.signal });
        if (!response.ok) throw new Error('Metrics unavailable');
        const next: MetricsResponse = await response.json();
        if (!stopped && startedRevision === revision) { data = next; error = false; }
      } catch { if (!stopped) error = true; }
      finally { clearTimeout(timeout); if (!stopped) timer = setTimeout(poll, 1000); }
    }
    void poll();
    return () => { stopped = true; clearTimeout(timer); controller?.abort(); };
  });
</script>

<svelte:head><title>valheim-boosted · Server observability</title></svelte:head>
<div class="shell">
  <aside class="sidebar">
    <div class="brand-mark">VB<span>↗</span></div>
    <div><strong>valheim-boosted</strong><p class="muted">World observability</p></div>
    <a class="nav-active" href="#overview">◈ <span>Overview</span></a>
    <a href="#connections">⇄ <span>Connections</span></a>
    <a href="#runtime">◷ <span>Resources &amp; scheduling</span></a>
    <a href="#ownership">◇ <span>Ownership</span></a>
    <a href="#improvements">↗ <span>Improvements</span></a>
    <a href="#diagnostics">◎ <span>Diagnostics</span></a>
    <a href="/reports">▤ <span>Reports</span></a>
    <a href="/history">◷ <span>History</span></a>
    <div class="sidebar-footer"><span class="eyebrow">{improvementsActive ? 'SERVER IMPROVEMENTS' : snapshot?.scheduler?.status === 'active' ? 'FAIR SCHEDULER' : 'OBSERVER MODE'}</span><p>{improvementsActive ? 'Replication experiments active. Compare results with a saved baseline.' : snapshot?.scheduler?.status === 'active' ? 'Experimental scheduling active. Watch service age and frame tails.' : 'Collecting measurements. Vanilla scheduling is retained.'}</p></div>
  </aside>
  <main id="overview">
    <header>
      <div><p class="eyebrow">YOUR WORLD, IN VIEW</p><h1>Server overview</h1><p class="muted">Connection health and simulation activity, together.</p></div>
      <div class="live-block"><span class="status {status}"><span class="dot"></span>{status}</span><small>{snapshot ? new Date(snapshot.capturedAtUtc).toLocaleTimeString() : 'No snapshot yet'}</small></div>
    </header>
    <ReportControls {data} onreset={(next) => { revision++; data = next; error = false; }} />
    {#if snapshot}<ClientTelemetry {snapshot} />{/if}
    {#if status !== 'live'}
      <div class="notice" role="status">{error ? 'Dashboard connection lost. Any displayed values are the last received measurements.' : data?.message ?? 'Connecting to the metrics service…'}</div>
    {/if}
    {#if blockedProbes > 0}<div class="notice" role="status">{blockedProbes} feature(s) unavailable. <a href="#diagnostics">Review compatibility and feature status.</a></div>{/if}
    {#if unavailablePeers.length > 0}<div class="notice" role="status">Connection measurements unavailable for {unavailablePeers.length} of {peers.filter(p => p.connected).length} connected peers in this snapshot. Missing values do not indicate a healthy connection. <a href="#connection-diagnostics">View error details.</a></div>{/if}
    <div class="cards">
      <section class="stat"><span class="eyebrow">CONNECTED PEERS</span><strong>{snapshot ? peers.length : '—'}</strong><small>Server-observed connections</small></section>
      <section class="stat"><span class="eyebrow">FRAME INTERVAL · P95</span><strong>{number(snapshot?.frameIntervalMs?.p95)} <em>ms</em></strong><small>Includes frame limiting and waits</small></section>
      <section class="stat"><span class="eyebrow">LOADED OBJECTS</span><strong>{number(snapshot?.loadedObjects, 0)}</strong><small>{number(snapshot?.knownZdos, 0)} known ZDOs</small></section>
      <section class="stat"><span class="eyebrow">MANAGED MEMORY</span><strong>{snapshot ? number(snapshot.managedMemoryBytes / 1048576, 0) : '—'} <em>MiB</em></strong><small>Excludes native / Unity allocations</small></section>
    </div>
    <div class="toolbar"><div class="segmented"><button class:chosen={metric === 'rttMs'} onclick={() => metric = 'rttMs'}>Latency</button><button class:chosen={metric === 'estimatedTransportQueueMs'} onclick={() => metric = 'estimatedTransportQueueMs'}>Queue delay</button></div><span class="muted">{data?.history.length ?? 0} retained samples · {selected ? 'Selected peer' : 'Up to 8 peers'}</span></div>
    <div class="charts"><MetricChart title={metric === 'rttMs' ? 'Connection latency' : 'Transport queue delay'} unit="ms" series={connections} /><MetricChart title="Simulation timing" unit="ms" series={timing} /></div>
    <section class="panel" id="connections">
      <div class="panel-heading"><div><h2>Connections</h2><p class="muted">Measured from this process. Select a peer to inspect its queues.</p></div>{#if selected}<button class="outline" onclick={() => selected = ''}>Show all</button>{/if}</div>
      <div class="table-scroll"><table><thead><tr><th>Peer session</th><th>RTT</th><th>Queue delay</th><th>Out / in</th><th>Outstanding</th><th>Measurement</th></tr></thead><tbody>
        {#each peers as peer (peer.peerSessionId)}<tr class:selected={selected === peer.peerSessionId}><td><button class="peer-button" title={peer.peerSessionId} onclick={() => selected = peer.peerSessionId}>{short(peer.peerSessionId)} ↗</button></td><td>{number(peer.rttMs)} ms</td><td>{number(peer.estimatedTransportQueueMs)} ms</td><td>{kib(peer.outgoingBytesPerSecond)} / {kib(peer.incomingBytesPerSecond)} KiB/s</td><td>{kib(peer.outstandingBytes)} KiB</td><td><span class="muted">{peer.measurementStatus}</span></td></tr>
        {:else}<tr><td colspan="6" class="empty">{snapshot ? 'No ready peers in this snapshot.' : 'Peer measurements will appear when the server exports telemetry.'}</td></tr>{/each}
      </tbody></table></div>
    </section>
    {#if active}<section class="panel detail"><div class="panel-heading"><h2>Peer {short(active.peerSessionId)}</h2><span class="eyebrow">{active.transport}</span></div><div class="detail-grid"><div><span>Application queue</span><strong>{kib(active.applicationQueuedBytes)} KiB</strong></div><div><span>Pending reliable</span><strong>{kib(active.pendingReliableBytes)} KiB</strong></div><div><span>Pending unreliable</span><strong>{kib(active.pendingUnreliableBytes)} KiB</strong></div><div><span>Sent, awaiting acknowledgment</span><strong>{kib(active.sentUnacknowledgedReliableBytes)} KiB</strong></div></div><p class="muted">Client CPU / frame timing: not reported. Outstanding data includes already-sent reliable bytes; it is not all waiting to leave the server.</p></section>{/if}
    {#if snapshot && (snapshot.scheduler || snapshot.resources)}<RuntimeMetrics {snapshot} history={data?.history ?? []} />{/if}
    {#if snapshot}<ServerImprovements {snapshot} />{/if}
    <section class="panel" id="ownership"><div class="panel-heading"><div><h2>Loaded-object ownership</h2><p class="muted">Ownership is distinct from who hosts the server.</p></div><span class="eyebrow">{snapshot?.ownershipStatus ?? 'Unavailable'}</span></div><div class="owners">{#each snapshot?.loadedOwnership ?? [] as owner}<div class="owner"><span>{owner.ownerSessionId === '0' ? 'Unowned' : 'Session ' + short(owner.ownerSessionId)}</span><strong>{number(owner.objects, 0)}</strong></div>{:else}<p class="muted">No loaded-object ownership measurements.</p>{/each}</div></section>
    <section class="panel" id="diagnostics">
      <div class="panel-heading"><div><h2>Diagnostic probes</h2><p class="muted">Game {snapshot?.compatibility?.gameVersion ?? 'unknown'} · Protocol {snapshot?.compatibility?.networkVersion ?? 'unknown'} · {snapshot?.compatibility?.status ?? 'Compatibility not reported'}</p></div></div>
      <p class="muted build-id">Mod build: {snapshot?.modBuildId ?? 'not reported by this mod build'}</p>
      <p class="muted">Steam interface: {snapshot?.compatibility?.steamInterface ?? 'Unavailable'}. Feature changes require restarting Valheim.</p>
      <div class="table-scroll"><table><thead><tr><th>Probe</th><th>Configured</th><th>Status</th><th>Calls / attempts</th><th>Details</th></tr></thead><tbody>
        {#each snapshot?.features ?? [] as feature (feature.id)}
          <tr><td>{feature.id}</td><td>{feature.enabled ? 'On' : 'Off'}</td><td>{feature.status}</td><td>{feature.target ? number(feature.invocations, 0) : '—'}</td><td>{feature.detail ?? feature.target ?? '—'}</td></tr>
        {:else}<tr><td colspan="5" class="empty">This snapshot does not include probe status.</td></tr>{/each}
      </tbody></table></div>
      <div class="connection-diagnostics" id="connection-diagnostics">
        <h2>Connection diagnostics</h2>
        <p class="muted">Live describes snapshot delivery. Individual measurements can still fail. Query failures are retried each sample; server warnings are limited to one every 30 seconds across all peers.</p>
        {#each unavailablePeers as peer (peer.peerSessionId)}
          <ConnectionDiagnostics {peer} />
        {:else}<p class="muted">{peers.length ? 'No connected peer measurement failures in this snapshot.' : 'Awaiting a connected peer.'}</p>{/each}
      </div>
    </section>
    <footer><span>valheim-boosted <span class="muted">/ {snapshot?.modVersion ?? 'awaiting mod'}</span></span><span>— means unavailable · Overview charts show recent samples</span></footer>
  </main>
</div>
