<script lang="ts">
  import DashboardShell from './DashboardShell.svelte';
  import { onMount } from 'svelte';
  import { comparisonWarnings, metricValue, reportMetrics, type ReportSummary, type SavedReport } from '../shared/reports';
  let reports = $state<ReportSummary[]>([]);
  let baseline = $state('');
  let candidate = $state('');
  let a = $state<SavedReport | null>(null);
  let b = $state<SavedReport | null>(null);
  let loading = $state(true);
  let comparing = $state(false);
  let error = $state('');
  let unreadable = $state(0);
  let showUnavailable = $state(false);
  let showCoverage = $state(false);
  const visibleMetrics = $derived(reportMetrics.filter(d => showUnavailable || a?.metrics[d.id] || b?.metrics[d.id]));
  const warnings = $derived(a && b ? comparisonWarnings(a, b) : []);
  const number = (n: number | null | undefined) => n == null ? '—' : n.toLocaleString(undefined, { maximumFractionDigits: 2 });
  const date = (s: string | null) => s ? new Date(s).toLocaleString() : '—';
  const delta = (left: number | null, right: number | null) => {
    if (left == null || right == null) return '—';
    const change = right - left;
    return `${change > 0 ? '+' : ''}${number(change)}${left === 0 ? ' · baseline zero' : ` (${change > 0 ? '+' : ''}${number(change / Math.abs(left) * 100)}%)`}`;
  };
  const coverage = (report: SavedReport, id: typeof reportMetrics[number]['id']) => {
    const m = report.metrics[id];
    return m ? `${m.windows}/${report.snapshots} windows · ${number(m.observations)} observations` : 'Unavailable';
  };
  const scheduler = (r: SavedReport) => r.source.scheduler
    ? `${r.source.scheduler.enabled ? 'On' : 'Off'} · ${r.source.scheduler.targetHz} Hz · ${r.source.scheduler.budgetMs} ms · ${r.source.scheduler.maxCallsPerFrame} calls/frame · debt ${r.source.scheduler.debtCap}` : 'Not reported';

  onMount(() => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);
    void (async () => {
      try {
        const response = await fetch('/api/reports', { signal: controller.signal, cache: 'no-store' });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error ?? 'Could not load reports.');
        reports = result.reports; unreadable = result.unreadable;
        const query = new URLSearchParams(location.search);
        baseline = reports.find(r => r.id === query.get('baseline'))?.id ?? reports.at(1)?.id ?? reports[0]?.id ?? '';
        candidate = reports.find(r => r.id === query.get('candidate'))?.id ?? reports.find(r => r.id !== baseline)?.id ?? '';
      } catch (e) { if (!controller.signal.aborted) error = e instanceof Error ? e.message : 'Could not load reports.'; else error = 'Loading reports timed out.'; }
      finally { clearTimeout(timer); loading = false; }
    })();
    return () => { clearTimeout(timer); controller.abort(); };
  });

  $effect(() => {
    const ids = [baseline, candidate];
    if (!ids.some(Boolean)) return;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);
    a = null; b = null; comparing = true; error = '';
    const query = new URLSearchParams();
    if (baseline) query.set('baseline', baseline);
    if (candidate) query.set('candidate', candidate);
    history.replaceState(null, '', `/reports?${query}`);
    void Promise.all(ids.map(async id => {
      if (!id) return null;
      const response = await fetch(`/api/reports/${id}`, { signal: controller.signal, cache: 'no-store' });
      const value = await response.json();
      if (!response.ok) throw new Error(value.error ?? 'Could not read report.');
      return value as SavedReport;
    })).then(([left, right]) => { if (!controller.signal.aborted) { a = left; b = right; } })
      .catch(e => { if (!controller.signal.aborted) error = e instanceof Error ? e.message : 'Could not read report.'; })
      .finally(() => { clearTimeout(timer); if (!controller.signal.aborted) comparing = false; });
    const timedOut = () => { error = 'Loading comparison timed out.'; comparing = false; };
    controller.signal.addEventListener('abort', timedOut, { once: true });
    return () => { controller.signal.removeEventListener('abort', timedOut); clearTimeout(timer); controller.abort(); };
  });
</script>

<svelte:head><title>Reports · valheim-boosted</title></svelte:head>
<DashboardShell page="reports">
    <header class="page-heading"><h1>Compare reports</h1><span class="muted">{reports.length} saved</span></header>
    {#if error}<p class="notice" role="alert">{error} <a href="/reports">Reload reports</a></p>{/if}
    {#if unreadable}<p class="notice">{unreadable} stored report(s) could not be read. Check the reports directory.</p>{/if}
    <section class="panel">
      <div class="panel-heading"><div><h2>Recordings</h2><p class="muted">Change = candidate − baseline</p></div></div>
      {#if loading}<p class="empty">Loading reports…</p>
      {:else if !reports.length}<div class="empty">No reports saved yet. Save a recording from the <a href="/">live overview</a>.</div>
      {:else}<div class="report-selectors">
        <label><span id="baseline-label">Baseline</span><select aria-labelledby="baseline-label" bind:value={baseline}><option value="">Choose a report</option>{#each reports as r}<option value={r.id}>{r.name} · {date(r.savedAtUtc)}</option>{/each}</select></label>
        <label><span id="candidate-label">Candidate</span><select aria-labelledby="candidate-label" bind:value={candidate}><option value="">Choose a report</option>{#each reports as r}<option value={r.id}>{r.name} · {date(r.savedAtUtc)}</option>{/each}</select></label>
      </div>{/if}
    </section>
    {#if comparing}<p class="muted" role="status">Loading comparison…</p>{/if}
    <div class="report-contexts">
      {#each [{ title: 'Baseline', report: a }, { title: 'Candidate', report: b }] as slot}
        {#if slot.report}{@const r = slot.report}
          <section class="panel report-context">
            <div class="panel-heading"><div><p class="eyebrow">{slot.title}</p><h2>{r.name}</h2></div><a class="outline" href={`/api/reports/${r.id}/download`}>JSON ↓</a></div>
            <div class="recording-facts"><p>{date(r.startedAtUtc)} → {date(r.endedAtUtc)}</p><p>{number(r.observedSeconds)} s observed · {r.snapshots} samples · {number(r.metrics.peers?.mean)} mean peers</p>{#if r.timeline}<a class="outline" href={`/history?report=${r.id}`}>View timeline ↗</a>{/if}</div>
            <details><summary>Recording details &amp; settings</summary><dl>
              {#if r.timelineError}<dt>Saved history</dt><dd>{r.timelineError}</dd>{/if}
              <dt>Recorded</dt><dd>{date(r.startedAtUtc)} → {date(r.endedAtUtc)}</dd>
              <dt>Coverage</dt><dd>{number(r.observedSeconds)} observed / {number(r.elapsedSeconds)} elapsed seconds · {r.snapshots} snapshots · {r.missedSnapshots} missed</dd>
              <dt>Peer count</dt><dd>{number(r.metrics.peers?.mean)} mean · {number(r.metrics.peers?.min)}–{number(r.metrics.peers?.max)} range</dd>
              <dt>Transport availability</dt><dd>{r.peerObservations ? `${number((r.peerObservations - r.unavailablePeerObservations) / r.peerObservations * 100)}% of peer observations` : 'No connected peers observed'}</dd>
              <dt>Mod build</dt><dd>{r.source.modVersion} · {r.source.modBuildId ?? 'Build ID unavailable'}</dd>
              <dt>Game / protocol</dt><dd>{r.source.compatibility?.gameVersion ?? 'Unknown'} / {r.source.compatibility?.networkVersion ?? 'Unknown'} · {r.source.role}</dd>
              <dt>Game module</dt><dd>{r.source.compatibility?.gameModuleId ?? 'Unknown'}</dd>
              <dt>World session</dt><dd>{r.source.worldSession ?? 'Unknown'}</dd>
              <dt>Scheduler</dt><dd>{scheduler(r)} · active in {r.schedulerActiveSnapshots}/{r.snapshots} windows</dd>
              {#if r.source.serverImprovements}{@const s = r.source.serverImprovements}
                <dt>Send windows</dt><dd>{s.windowsEnabled ? 'On' : 'Off'} · {number(s.maximumWindowBytes / 1024)} KiB maximum · {number(s.targetBytesPerSecond / 1024)} KiB/s target ceiling</dd>
                <dt>Steam maximum rate</dt><dd>{s.rateEnabled ? 'On' : 'Off'} · {number(s.requestedMaxRateBytesPerSecond / 1024)} KiB/s requested</dd>
                <dt>Captain ownership</dt><dd>{s.captainEnabled ? 'On' : 'Off'}</dd>
                <dt>Compression</dt><dd>{s.compressionEnabled ? 'On' : 'Off'} · {s.compressionBudgetMs} ms/frame soft encode budget</dd>
              {/if}
              <dt>Feature switches</dt><dd>{r.source.features.map(f => `${f.id}: ${f.enabled ? 'on' : 'off'}`).join(' · ') || 'Not reported'}</dd>
              <dt>At save</dt><dd>{r.telemetryStatus}{r.pausedReason ? ' · recording paused at session/settings change' : ''}</dd>
            </dl></details>
          </section>
        {/if}
      {/each}
    </div>
    {#if warnings.length}<div class="notice comparison-warnings"><strong>Comparison context</strong><ul>{#each warnings as warning}<li>{warning}</li>{/each}</ul></div>{/if}
    {#if a || b}
      <section class="panel">
        <div class="panel-heading measurement-heading"><div><h2>Measurements</h2><p class="muted">Observed differences; not proof of cause.</p></div><div class="measurement-options"><label class="checkbox-label"><input type="checkbox" bind:checked={showCoverage} />Show coverage</label><label class="checkbox-label"><input type="checkbox" bind:checked={showUnavailable} />Show unavailable metrics</label></div></div>
        <div class="table-scroll"><table class="comparison-table"><thead><tr><th>Metric</th><th>Baseline</th><th>Candidate</th><th>Change</th></tr></thead><tbody>
          {#each visibleMetrics as definition}
            {@const left = a ? metricValue(a, definition) : null}
            {@const right = b ? metricValue(b, definition) : null}
            <tr><td>{definition.label}<small>{definition.unit || 'count'}</small></td>
              <td>{number(left)}{#if showCoverage}<small>{a ? coverage(a, definition.id) : 'No report selected'}</small>{/if}{#if showCoverage && definition.mode === 'rate' && a?.metrics[definition.id]}<small>{number(a.metrics[definition.id]!.total)} observed total</small>{/if}</td>
              <td>{number(right)}{#if showCoverage}<small>{b ? coverage(b, definition.id) : 'No report selected'}</small>{/if}{#if showCoverage && definition.mode === 'rate' && b?.metrics[definition.id]}<small>{number(b.metrics[definition.id]!.total)} observed total</small>{/if}</td>
              <td>{delta(left, right)}</td></tr>
          {/each}
        </tbody></table></div>
      </section>
      <details class="measurement-notes"><summary>How comparisons are calculated</summary><p>Timing means are weighted by event counts. Other means use available observations; peer means pool connected peers. Counter rates use observed seconds with measurements. Missing values stay unavailable. Worst-window p95/p99 are the highest exported window percentiles, not whole-recording percentiles. Reports summarize the full window independently of the bounded live charts.</p></details>
    {/if}

</DashboardShell>
