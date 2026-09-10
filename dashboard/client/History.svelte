<script lang="ts">
  import DashboardShell from './DashboardShell.svelte';
  import { onMount } from 'svelte';
  import MetricChart from './MetricChart.svelte';
  import { ChartGroup } from 'layerchart';
  import { archiveHistory,archiveSeries } from '../shared/history-view';
  import { historyMetrics, type HistoryArchive, type HistoryMetric, type HistoryResult, type HistorySeries, type HistoryStatus, type PlayerSession } from '../shared/history';
  import type { SavedReport } from '../shared/reports';
  const local = (ms: number) => new Date(ms - new Date(ms).getTimezoneOffset() * 60000).toISOString().slice(0, 19);
  let from = $state(local(Date.now() - 900000)), to = $state(local(Date.now()));
  let live = $state(true), metric = $state<HistoryMetric>('frameP95Ms');
  let sources = $state<HistorySeries[]>([]), selected = $state<string[]>([]);
  let results = $state<HistoryResult[]>([]), status = $state<HistoryStatus | null>(null);
  let error = $state(''), loading = $state(false), cursor = $state<number | null>(null), truncated = $state(false);
  let archived = $state<HistoryArchive | null>(null), reportName = $state('');
  let groupPlayers=$state(true),sessions=$state<PlayerSession[]>([]),sessionsTruncated=$state(false);
  let manualSelection = false, controller: AbortController | undefined, generation = 0;
  const definition = $derived(historyMetrics.find(m => m.key === metric)!);
  const chosen = $derived(sources.filter(s => selected.includes(s.id)));
  const events = $derived(results[0]?.markers ?? []);
  const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const number = (n: number | null | undefined) => n == null ? '—' : n.toLocaleString(undefined, { maximumFractionDigits: 2 });
  const date = (n: number | null | undefined) => n == null ? '—' : new Date(n).toLocaleString();
  function data(result: HistoryResult | undefined) {
    const order = {server:0,client:1,connection:2};
    return [...(result?.series ?? [])].sort((a,b)=>order[a.info.kind]-order[b.info.kind] || a.info.id.localeCompare(b.info.id)).map(s => {
      const points: [number, number | null][] = [[result!.fromMs, null]];
      let last = result!.fromMs;
      for (const p of s.points) { if (p.at - last > result!.stepMs * 1.5) points.push([last + result!.stepMs, null]); points.push([p.at, p.value]); last = p.at; }
      points.push([result!.toMs, null]);
      return { key:s.info.id, name: `${s.info.name} · ${(s.info.grouped ? s.info.playerId! : s.info.id).slice(-6)}`, data: points };
    });
  }
  function value(result: HistoryResult | undefined, seriesId: string) {
    if (!result || cursor == null) return null;
    const at = Math.floor(cursor / result.stepMs) * result.stepMs;
    return result.series.find(s => s.info.id === seriesId)?.points.find(p => p.at === at) ?? null;
  }
  function archiveResult(key: HistoryMetric, start: number, end: number): HistoryResult {
    return archiveHistory(archived!,chosen,key,start,end);
  }
  async function read(path: string, signal: AbortSignal) {
    const response = await fetch(path, { cache: 'no-store', signal }); const result = await response.json();
    if (!response.ok) throw new Error(result.error ?? 'History unavailable'); return result;
  }
  async function refresh() {
    const run = ++generation; controller?.abort(); controller = new AbortController();
    const signal = controller.signal, timeout = setTimeout(() => controller?.abort(), 20000);
    loading = true; error = '';
    try {
      if (live && !archived) { const width = new Date(to).getTime() - new Date(from).getTime(); to = local(Date.now()); from = local(Date.now() - (width > 0 ? width : 900000)); }
      const start = new Date(from).getTime(), end = new Date(to).getTime();
      if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) throw new Error('Choose an end time after the start time.');
      if (!archived) {
        const [listing, health] = await Promise.all([read(`/api/history/series?from=${start}&to=${end}&group=${groupPlayers?'player':'session'}`, signal), read('/api/history/status', signal)]);
        if (run !== generation) return;
        sources = listing.series; truncated = listing.truncated; status = health;
        if (health.error) error = health.error;
      } else sources = archiveSeries(archived,groupPlayers,start,end);
      if (!manualSelection) {
        const peer = new URLSearchParams(location.search).get('peer');
        const player = new URLSearchParams(location.search).get('player');
        const server = sources.find(s => s.kind === 'server');
        const clients = sources.filter(s => s.kind === 'client' && (player?s.playerId===player:!peer || s.peerSessionId === peer)).slice(0, 3);
        const connections = sources.filter(s => s.kind === 'connection' && (player?s.playerId===player:peer ? s.peerSessionId === peer : clients.length ? clients.some(c => c.playerId ? c.playerId===s.playerId : c.peerSessionId === s.peerSessionId && c.processSession === s.processSession) : true)).slice(0, 3);
        selected = [...(server ? [server.id] : []), ...clients.map(s => s.id), ...connections.map(s => s.id)];
      }
      const metrics: HistoryMetric[] = [metric, 'rttMs', 'networkP95Ms'];
      const targets=sources.filter(s=>selected.includes(s.id));
      const singles=targets.filter(s=>!s.grouped).map(s=>s.id).join(','),players=targets.filter(s=>s.grouped).map(s=>`${s.playerId}:${s.kind}`).join(',');
      const playerIds=[...new Set(targets.flatMap(s=>s.playerId?[s.playerId]:[]))];
      if(archived){sessions=(archived.sessions??[]).filter(s=>playerIds.includes(s.playerId??'')&&s.startedMs<=end&&(s.endedMs??s.lastSeenMs)>=start);sessionsTruncated=false;}
      else {const listing=await read(`/api/history/sessions?from=${start}&to=${end}&players=${playerIds.join(',')}`,signal);if(run!==generation)return;sessions=listing.sessions;sessionsTruncated=listing.truncated;}
      const next = archived ? metrics.map(m => archiveResult(m, start, end))
        : await Promise.all(metrics.map(m => read(`/api/history?from=${start}&to=${end}&metric=${m}&series=${singles}&players=${players}&points=900`, signal))) as HistoryResult[];
      if (run === generation) {
        results = next;
        if (cursor == null || cursor < start || cursor > end) {
          const times = next[0].series.flatMap(s => s.points.filter(p => p.value != null).map(p => p.at));
          cursor = times.length ? Math.max(start, ...times) : end;
        }
      }
    } catch (e) { if (run === generation) error = signal.aborted ? 'History request timed out. Narrow the range or retry.' : e instanceof Error ? e.message : 'History unavailable.'; }
    finally { clearTimeout(timeout); if (run === generation) loading = false; }
  }
  function range(ms: number) { from = local(Date.now() - ms); to = local(Date.now()); live = true; cursor = null; void refresh(); }
  function toggle(id: string, enabled: boolean) { manualSelection = true; selected = enabled ? [...selected, id] : selected.filter(s => s !== id); void refresh(); }
  onMount(() => {
    let stopped = false;
    const report = new URLSearchParams(location.search).get('report');
    const init = new AbortController();
    const initTimeout = setTimeout(() => init.abort(), 15000);
    void (async () => {
      try {
        if (report) {
          live = false; const saved: SavedReport = await read(`/api/reports/${encodeURIComponent(report)}`, init.signal);
          if (stopped) return;
          if (!saved.timeline) throw new Error(saved.timelineError ?? 'This report has no saved timeline.');
          archived = saved.timeline; reportName = saved.name; from = local(archived.fromMs); to = local(Math.ceil(archived.toMs / 1000) * 1000);
        }
        if (!stopped) await refresh();
      } catch (e) { if (!stopped) error = e instanceof Error ? e.message : 'Could not load history.'; }
      finally { clearTimeout(initTimeout); }
    })();
    const interval = setInterval(() => { if (live && !loading && !archived) void refresh(); }, 10000);
    return () => { stopped = true; generation++; clearInterval(interval); clearTimeout(initTimeout); controller?.abort(); init.abort(); };
  });
</script>

<svelte:head><title>History · valheim-boosted</title></svelte:head>
<DashboardShell page="history">
    <header class="page-heading"><div><h1>{archived ? 'Saved timeline' : 'History'}</h1><p class="muted">{archived ? reportName : `${status?.retentionDays ?? 7}-day history`} · {timezone}</p></div></header>
    {#if error}<p class="notice" role="alert">{error}</p>{/if}
    {#if archived}<p class="notice">Saved at {number(archived.stepMs / 1000)}-second resolution. Peaks and the first lag marker are retained per bucket; a saved timeline can be coarser than live history.{#if archived.seriesTruncated} Some series were omitted at the archive limit.{/if}{#if archived.retainedFromMs != null && archived.retainedFromMs > archived.fromMs} Earlier history had already expired when this report was saved.{/if}</p>{/if}
    <section class="panel">
      <div class="panel-heading"><h2>Time range</h2><span class="muted">{loading ? 'Loading…' : `${sources.length} available series`}</span></div>
      <form class="history-controls" onsubmit={e => { e.preventDefault(); live = false; void refresh(); }}>
        <label>From<input type="datetime-local" step="1" bind:value={from} required /></label><label>To<input type="datetime-local" step="1" bind:value={to} required /></label><button class="primary" type="submit" disabled={loading}>Apply range</button>
      </form>
      {#if !archived}<div class="history-presets"><button class="outline" onclick={() => range(900000)}>15 minutes</button><button class="outline" onclick={() => range(3600000)}>1 hour</button><button class="outline" onclick={() => range(86400000)}>24 hours</button><button class="outline" onclick={() => range((status?.retentionDays ?? 7) * 86400000)}>Retention window</button><label class="checkbox-label"><input type="checkbox" bind:checked={live} />Follow live</label></div>{/if}
      <div class="history-grouping"><label class="checkbox-label"><input type="checkbox" bind:checked={groupPlayers} onchange={()=>{manualSelection=false;void refresh();}} />Combine returning players across sessions</label></div>
      <details class="history-sources"><summary>Players &amp; sessions · {selected.length}/9 series</summary>
        {#if truncated}<p class="muted">Showing the most recent 256 series. Narrow the time range to find older sessions.</p>{/if}
        <div class="source-grid">{#each sources as s (s.id)}<label class="checkbox-label"><input type="checkbox" checked={selected.includes(s.id)} disabled={!selected.includes(s.id) && selected.length >= 9} onchange={e => toggle(s.id, e.currentTarget.checked)} /><span>{s.name}<small>{s.grouped?`${s.sessionCount} sessions combined`:s.kind} · last seen {date(s.lastMs)} · {(s.playerId??s.id).slice(-6)}</small></span></label>{:else}<p class="muted">No history in this range.</p>{/each}</div>
      </details>
    </section>
    <div class="history-metric"><label><span id="history-metric-label">Compare</span><select aria-labelledby="history-metric-label" bind:value={metric} onchange={() => void refresh()}>{#each historyMetrics as m}<option value={m.key}>{m.label}</option>{/each}</select></label><p class="muted">Click a chart to inspect a moment.</p></div>
    <ChartGroup pointer={{tooltip:false}} brush={false} domain={false} series={false}>
      <div class="history-main-chart"><MetricChart title={definition.label} unit={definition.unit} series={data(results[0])} onselect={at => cursor = at} /></div>
      <div class="charts"><MetricChart title="Connection RTT · both perspectives" unit="ms" series={data(results[1])} onselect={at => cursor = at} /><MetricChart title="ZDO update duration · worst window p95" unit="ms" series={data(results[2])} onselect={at => cursor = at} /></div>
    </ChartGroup>
    <section class="panel range-summary"><div class="panel-heading"><div><h2>Range summary</h2><p class="muted">Observed time only; offline gaps excluded.</p></div></div>
      <div class="table-scroll"><table><thead><tr><th>Player / perspective</th><th>{definition.label}</th><th>RTT</th><th>Observed time</th><th>Sessions</th></tr></thead><tbody>
        {#each chosen as s}{@const summary=results[0]?.series.find(r=>r.info.id===s.id)?.summary}<tr><td>{s.name}<small>{s.playerId?`Player ${s.playerId.slice(-6)}`:'Session-specific data'}</small></td><td>{number(summary?.value)} {definition.unit.replace('/bucket','')}</td><td>{number(results[1]?.series.find(r=>r.info.id===s.id)?.summary?.value)} ms</td><td>{number(summary ? summary.observedMs/1000 : null)} s</td><td>{s.sessionCount??(s.sessionId?1:'—')}</td></tr>{:else}<tr><td colspan="5" class="empty">No measurements in the selected range.</td></tr>{/each}
      </tbody></table></div>
    </section>
    <section class="panel">
      <div class="panel-heading"><div><h2>At this time</h2><p class="muted">{date(cursor)} · {number((results[0]?.stepMs ?? 1000) / 1000)}-second buckets</p></div><label>Inspect time<input type="datetime-local" step="1" value={cursor == null ? '' : local(cursor)} onchange={e => { const n = new Date(e.currentTarget.value).getTime(); if (Number.isFinite(n)) cursor = n; }} /></label></div>
      <div class="table-scroll"><table><thead><tr><th>Perspective</th><th>{definition.label}</th><th>RTT</th><th>ZDO update p95</th><th>Coverage</th><th>Clock uncertainty</th></tr></thead><tbody>
        {#each chosen as s}{@const point = value(results[0], s.id)}<tr><td>{s.name}</td><td>{number(point?.value)} {definition.unit}</td><td>{number(value(results[1], s.id)?.value)} ms</td><td>{number(value(results[2], s.id)?.value)} ms</td><td>{point ? `${point.samples} samples · ${number(point.observedMs / 1000)} observed s` : 'No sample in bucket'}</td><td>{point?.clockErrorMs == null ? '—' : `±${number(point.clockErrorMs)} ms`}</td></tr>{:else}<tr><td colspan="6" class="empty">Select a series to inspect its measurements.</td></tr>{/each}
      </tbody></table></div>
    </section>
    <section class="panel player-sessions"><div class="panel-heading"><div><h2>Player sessions</h2><p class="muted">Each login is listed separately.</p></div><span class="muted">{sessions.length} sessions</span></div>
      <div class="table-scroll"><table><thead><tr><th>Player / character</th><th>Session started</th><th>Last observed</th><th>Ended</th><th>State</th></tr></thead><tbody>
        {#each sessions as s (s.id)}<tr><td>{s.name}<small>{s.playerId ? `Player ${s.playerId.slice(-6)}` : 'Identity unavailable'} · session {s.id.slice(-6)}</small></td><td>{date(s.startedMs)}</td><td>{date(s.lastSeenMs)}</td><td>{date(s.endedMs)}</td><td>{s.endReason==='disconnect'?'Disconnected':s.endReason==='server_stopped'?'Server stopped':s.endReason?'End estimated':archived?'Open when saved':Date.now()-s.lastSeenMs>10000?'Last state unknown':'Connected'}</td></tr>{:else}<tr><td colspan="5" class="empty">Select a player with stable identity to inspect their sessions.</td></tr>{/each}
      </tbody></table></div>{#if sessionsTruncated}<p class="runtime-note muted">Showing 256 sessions; narrow the time range.</p>{/if}
    </section>
    <section class="panel"><div class="panel-heading"><div><h2>Player lag markers <span class="muted">({events.length})</span></h2><p class="muted">F9 markers may appear after a freeze ends.</p></div></div><div class="history-markers">{#each events as e}<button class="outline" onclick={() => cursor = e.at}>{sources.find(s => s.id === e.seriesId)?.name ?? 'Client'} · {date(e.at)} · ±{number(e.clockErrorMs)} ms</button>{:else}<p class="muted">No markers in the selected range and series.</p>{/each}{#if results[0]?.markersTruncated}<p class="muted">Marker limit reached; narrow the time range.</p>{/if}</div></section>
    <details class="measurement-notes"><summary>About these measurements</summary><p>Returning players are grouped by server-scoped Steam identity. Older data without identity stays separate. Estimated session endings use the last observed time. Window percentiles are not whole-session percentiles.</p><p>Client values are self-reported. Focus, frame limits, loading, GC, and shared workload can explain coincident slowdowns; correlation does not establish which player or process caused lag. Clock uncertainty includes round-trip delay and an allowance for drift. Server frame intervals include waiting and frame limiting.</p></details>
    <footer><span>{archived ? 'Saved independently of live retention' : `Database ${number((status?.databaseBytes ?? 0) / 1048576)} MiB · ${status?.droppedOffers ?? 0} history offers replaced while busy`}</span></footer>
</DashboardShell>
