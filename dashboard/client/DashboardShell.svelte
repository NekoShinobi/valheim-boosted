<script lang="ts">
  import type { Snippet } from 'svelte';
  let { page, children }: { page: 'live' | 'history' | 'reports'; children: Snippet } = $props();
  const pages = [
    { id: 'live', href: '/', label: 'Live' },
    { id: 'history', href: '/history', label: 'History' },
    { id: 'reports', href: '/reports', label: 'Reports' },
  ];
</script>

<div class="shell">
  <a class="skip-link" href="#main-content">Skip to content</a>
  <div class="topbar">
    <div class="topbar-inner">
      <a class="brand" href="/" aria-label="valheim-boosted home">
        <img src="/favicon.svg" width="36" height="36" alt="" />
        <span>valheim<span class="brand-suffix">-boosted</span></span>
      </a>
      <nav class="primary-nav" aria-label="Main navigation">
        {#each pages as item}
          <a href={item.href} aria-current={page === item.id ? 'page' : undefined}>{item.label}</a>
        {/each}
      </nav>
    </div>
  </div>
  <main id="main-content" tabindex="-1">{@render children()}</main>
</div>
