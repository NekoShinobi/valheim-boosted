<script lang="ts">
  import type { Peer } from '../shared/telemetry';
  let { peer }: { peer: Peer } = $props();
</script>

<article class="connection-error">
  <h3>Peer {peer.peerSessionId} <span class="muted">· {peer.transport}</span></h3>
  <p class="error-status">{peer.measurementStatus}</p>
  {#if peer.measurementError}
    <dl>
      <dt>Underlying error</dt><dd>{peer.measurementError.exceptionType}: {peer.measurementError.message}</dd>
      <dt>Operation</dt><dd>{peer.measurementError.operation}</dd>
      <dt>Exception chain</dt><dd>{peer.measurementError.exceptionChain}</dd>
    </dl>
    <p class="muted">The BepInEx server log contains the exception stack trace. Share this error and the matching “Steam telemetry failed” log entry when reporting a problem.</p>
  {:else if peer.measurementStatus.startsWith('unavailable:')}
    <p class="muted">This snapshot does not include the underlying exception. Install the updated mod and restart Valheim to export error details; a dashboard update alone cannot reveal the cause.</p>
  {:else}
    <p class="muted">Review the probe configuration and status above. Native Steam query failures include their result code in the measurement status.</p>
  {/if}
</article>
