<script lang="ts">
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
<div class="shell">
  <aside class="sidebar">
    <div class="brand-mark">VB<span>↗</span></div>
    <div><strong>valheim-boosted</strong><p class="muted">World observability</p></div>
    <a href="/">◈ <span>Overview</span></a>
    <a class="nav-active" href="/reports">▤ <span>Reports</span></a>
    <div class="sidebar-footer"><span class="eyebrow">BEFORE &amp; AFTER</span><p>Match the world, player activity and recording length. Change one setting at a time.</p></div>
  </aside>
  <main>
    <header><div><p class="eyebrow">FROM GUESSWORK TO EVIDENCE</p><h1>Compare reports</h1><p class="muted">Saved measurements, with the context behind each change.</p></div><a class="outline" href="/">← Live overview</a></header>
    {#if error}<p class="notice" role="alert">{error} <a href="/reports">Reload reports</a></p>{/if}
    {#if unreadable}<p class="notice">{unreadable} stored report(s) could not be read. Check the JSON files in the server's reports directory.</p>{/if}
    <section class="panel">
      <div class="panel-heading"><div><h2>Choose your recordings</h2><p class="muted">{reports.length} saved reports · changes are candidate minus baseline</p></div></div>
      {#if loading}<p class="empty">Loading reports…</p>
      {:else if !reports.length}<div class="empty">No reports saved yet. Name and save a recording on the <a href="/">live overview</a>, then reset stats for your next test.</div>
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
            <dl>
              <dt>Recorded</dt><dd>{date(r.startedAtUtc)} → {date(r.endedAtUtc)}</dd>
              <dt>Coverage</dt><dd>{number(r.observedSeconds)} observed / {number(r.elapsedSeconds)} elapsed seconds · {r.snapshots} snapshots · {r.missedSnapshots} missed</dd>
              <dt>Peer count</dt><dd>{number(r.metrics.peers?.mean)} mean · {number(r.metrics.peers?.min)}–{number(r.metrics.peers?.max)} range</dd>
              <dt>Transport availability</dt><dd>{r.peerObservations ? `${number((r.peerObservations - r.unavailablePeerObservations) / r.peerObservations * 100)}% of peer observations` : 'No connected peers observed'}</dd>
              <dt>Mod build</dt><dd>{r.source.modVersion} · {r.source.modBuildId ?? 'Build ID unavailable'}</dd>
              <dt>Game / protocol</dt><dd>{r.source.compatibility?.gameVersion ?? 'Unknown'} / {r.source.compatibility?.networkVersion ?? 'Unknown'} · {r.source.role}</dd>
              <dt>Game module</dt><dd>{r.source.compatibility?.gameModuleId ?? 'Unknown'}</dd>
              <dt>World session</dt><dd>{r.source.worldSession ?? 'Unknown'}</dd>
              <dt>Scheduler</dt><dd>{scheduler(r)} · active in {r.schedulerActiveSnapshots}/{r.snapshots} windows</dd>
              <dt>Feature switches</dt><dd>{r.source.features.map(f => `${f.id}: ${f.enabled ? 'on' : 'off'}`).join(' · ') || 'Not reported'}</dd>
              <dt>At save</dt><dd>{r.telemetryStatus}{r.pausedReason ? ' · recording paused at session/settings change' : ''}</dd>
            </dl>
          </section>
        {/if}
      {/each}
    </div>
    {#if warnings.length}<div class="notice comparison-warnings"><strong>Comparison context</strong><ul>{#each warnings as warning}<li>{warning}</li>{/each}</ul></div>{/if}
    {#if a || b}
      <section class="panel">
        <div class="panel-heading measurement-heading"><div><h2>Measurements</h2><p class="muted">Differences describe observations; they do not establish the cause of a change.</p></div><label class="checkbox-label"><input type="checkbox" bind:checked={showUnavailable} />Show unavailable metrics</label></div>
        <div class="table-scroll"><table class="comparison-table"><thead><tr><th>Metric</th><th>Baseline</th><th>Candidate</th><th>Change</th></tr></thead><tbody>
          {#each visibleMetrics as definition}
            {@const left = a ? metricValue(a, definition) : null}
            {@const right = b ? metricValue(b, definition) : null}
            <tr><td>{definition.label}<small>{definition.unit || 'count'}</small></td>
              <td>{number(left)}<small>{a ? coverage(a, definition.id) : 'No report selected'}</small>{#if definition.mode === 'rate' && a?.metrics[definition.id]}<small>{number(a.metrics[definition.id]!.total)} observed total</small>{/if}</td>
              <td>{number(right)}<small>{b ? coverage(b, definition.id) : 'No report selected'}</small>{#if definition.mode === 'rate' && b?.metrics[definition.id]}<small>{number(b.metrics[definition.id]!.total)} observed total</small>{/if}</td>
              <td>{delta(left, right)}</td></tr>
          {/each}
        </tbody></table></div>
      </section>
      <p class="muted">Timing means are weighted by event counts. Other means use available observations; peer means pool connected peers. Counter rates use observed seconds with measurements. Missing values stay unavailable. Worst-window p95/p99 are the highest exported window percentiles, not whole-recording percentiles. Reports summarize the full window independently of the bounded live charts.</p>
    {/if}
    <footer><span>valheim-boosted / Reports</span><span>Saved on the metrics server · Share this page's URL or download JSON</span></footer>
  </main>
</div>
