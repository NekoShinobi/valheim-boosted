<script lang="ts">
  import type { MetricsResponse } from '../shared/telemetry';
  import type { SavedReport } from '../shared/reports';
  let { data, onreset }: { data: MetricsResponse | null; onreset: (next: MetricsResponse) => void } = $props();
  let name = $state('');
  let busy = $state(false);
  let message = $state('');
  let failed = $state(false);
  let saved = $state<SavedReport | null>(null);
  const recording = $derived(data?.recording);
  async function action(reset: boolean) {
    busy = true; message = ''; failed = false; saved = null;
    try {
      const response = await fetch(reset ? '/api/recording/reset' : '/api/reports', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(reset ? {} : { name }), signal: AbortSignal.timeout(15000),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error ?? 'Request failed.');
      if (reset) { onreset(result); message = 'Stats reset. Waiting for the next complete sample window.'; }
      else { saved = result; message = 'Report saved. Recording continues until you reset stats.'; }
    } catch (error) {
      failed = true;
      message = error instanceof Error && error.name === 'TimeoutError'
        ? 'Request timed out. Check saved reports or the recording window before retrying.'
        : error instanceof Error ? error.message : 'Metrics service unavailable.';
    } finally { busy = false; }
  }
</script>

<section class="panel recording-panel" aria-label="Report recording">
  <div class="panel-heading">
    <div><p class="eyebrow">MEASURE A CHANGE</p><h2>Recording window</h2><p class="muted">{recording?.snapshots ?? 0} snapshots · {(recording?.observedSeconds ?? 0).toFixed(1)} observed seconds{#if recording?.startedAtUtc} · since {new Date(recording.startedAtUtc).toLocaleTimeString()}{/if}</p></div>
    <a class="outline" href="/reports">Compare reports ↗</a>
  </div>
  <form class="report-actions" onsubmit={(event) => { event.preventDefault(); void action(false); }}>
    <label>Report name<input bind:value={name} maxlength="100" placeholder="e.g. Vanilla · meadow base" required /></label>
    <button class="primary" type="submit" disabled={busy || !recording?.snapshots || !name.trim()}>Save report</button>
    <button class="outline" type="button" disabled={busy} onclick={() => void action(true)}>Reset stats</button>
  </form>
  <p class="muted runtime-note">Reset clears unsaved report stats and chart history for everyone using this dashboard. Live game counters and saved reports remain unchanged.</p>
  {#if recording?.pausedReason}<p class="notice report-message">{recording.pausedReason}</p>{/if}
  {#if message}<div class:report-error={failed} class="report-message" role="status">{message}{#if saved} <a href={`/api/reports/${saved.id}/download`}>Download JSON</a> · <a href={`/reports?baseline=${saved.id}`}>View report</a>{/if}</div>{/if}
</section>
