<script lang="ts">
  import type { Snapshot } from '../shared/telemetry';
  let { snapshot }: { snapshot: Snapshot } = $props();
  const metrics = $derived(snapshot.serverImprovements);
  const number = (v: number | null | undefined, digits = 1) => v == null ? '—' : v.toLocaleString(undefined, { maximumFractionDigits: digits });
  const kib = (v: number | null | undefined) => v == null ? '—' : number(v / 1024);
  const label = (v: string | null | undefined) => v?.replaceAll('_', ' ') ?? 'Not reported';
  const status = (id: string) => label(snapshot.features?.find(f => f.id === id)?.status);
  const saved = $derived(metrics && metrics.rawPayloadBytes > 0 ? (1 - metrics.framedPayloadBytes / metrics.rawPayloadBytes) * 100 : null);
</script>

{#if metrics}
  <section class="panel" id="improvements">
    <div class="panel-heading"><div><p class="eyebrow">EXPERIMENTAL · STAGES 3–5</p><h2>Server improvements</h2><p class="muted">Configured switches and observed behavior. Counters cover the latest sample window.</p></div><a class="outline" href="/history">Explore history ↗</a></div>
    <div class="improvement-cards">
      <article><span class="eyebrow">03 · SEND WINDOWS</span><strong>{metrics.windowsEnabled ? 'Enabled' : 'Off'}</strong><small>{status('SendWindows')}</small><p>Up to {kib(metrics.maximumWindowBytes)} KiB per peer<br />Target ceiling {kib(metrics.targetBytesPerSecond)} KiB/s</p></article>
      <article><span class="eyebrow">03 · STEAM RATE</span><strong>{metrics.rateEnabled ? 'Enabled' : 'Off'}</strong><small>{status('SteamRate')}</small><p>Requested max {kib(metrics.requestedMaxRateBytesPerSecond)} KiB/s<br />Connection overrides with readback</p></article>
      <article><span class="eyebrow">04 · CAPTAIN OWNERSHIP</span><strong>{number(metrics.captainTransfers, 0)} <em>transfers</em></strong><small>{label(metrics.captainStatus)}</small><p>{metrics.captainCandidates} eligible captains<br />{metrics.captainDeferred} deferred by work limits</p></article>
      <article><span class="eyebrow">05 · COMPRESSION</span><strong>{saved == null ? '—' : number(saved)} <em>% saved</em></strong><small>{status('Compression')}</small><p>{kib(metrics.rawPayloadBytes - metrics.framedPayloadBytes)} KiB saved on compressed sends<br />{metrics.compressedSent} sent · {metrics.compressedReceived} received</p></article>
    </div>
    <div class="improvement-cost"><span>Encode p95 <b>{number(metrics.compressionEncodeMs?.p95, 3)} ms</b></span><span>Decode p95 <b>{number(metrics.compressionDecodeMs?.p95, 3)} ms</b></span><span>Soft encode budget <b>{number(metrics.compressionBudgetMs)} ms/frame</b></span><span>Skipped / rejected <b>{metrics.compressionSkipped} / {metrics.compressionRejected}</b></span></div>
    <p class="muted">Savings include compression framing and session tokens, before Steam overhead. Small, incompressible or over-budget batches use the vanilla RPC. Unmodded peers keep the vanilla format.</p>
    <div class="table-scroll"><table><thead><tr><th>Peer session</th><th>ZDO allowance</th><th>Steam max · original → effective</th><th>Minimum rate</th><th>Compression</th></tr></thead><tbody>
      {#each snapshot.peers as peer (peer.peerSessionId)}
        <tr><td title={peer.peerSessionId}>…{peer.peerSessionId.slice(-8)}</td><td>{kib(peer.improvements?.windowBytes)} KiB<small>{label(peer.improvements?.windowStatus)}</small></td><td>{kib(peer.improvements?.originalMaxRateBytesPerSecond)} → {kib(peer.improvements?.effectiveMaxRateBytesPerSecond)} KiB/s<small>{label(peer.improvements?.rateStatus)}{#if peer.improvements?.rateWriteFailures} · {peer.improvements.rateWriteFailures} failures{/if}</small></td><td>{kib(peer.improvements?.minimumRateBytesPerSecond)} KiB/s</td><td>{label(peer.improvements?.compressionStatus)}</td></tr>
      {:else}<tr><td colspan="5" class="empty">Waiting for ready connections.</td></tr>{/each}
    </tbody></table></div>
    {#if metrics.steamConfigInterface}<p class="muted build-id">Steam config interface: {metrics.steamConfigInterface}</p>{/if}
  </section>
{/if}

<style>
  .improvement-cards { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin: 20px 24px; }
  article { border: 1px solid var(--line, #263833); background: #101e1b; border-radius: 12px; padding: 18px; min-width: 0; }
  article strong { display: block; font-size: 1.65rem; margin: 12px 0 4px; color: #c8f8df; }
  article em { font-size: .85rem; font-style: normal; font-weight: 400; }
  article small { display: block; color: #99c3b2; overflow-wrap: anywhere; }
  article p { color: #8ea99d; font-size: .78rem; line-height: 1.7; margin-bottom: 0; }
  .improvement-cost { display: flex; flex-wrap: wrap; gap: 12px 24px; padding: 0 24px; font-size: .8rem; color: #93afa4; }
  .panel > p { padding: 0 24px; }
  .improvement-cost b { color: #d4e9df; margin-left: 5px; font-weight: 500; }
  td small { display: block; color: #8ea99d; margin-top: 5px; }
  @media (max-width: 1250px) { .improvement-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  @media (max-width: 550px) { .improvement-cards { grid-template-columns: 1fr; } }
</style>
