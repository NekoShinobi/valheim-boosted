# Changelog

## v0.2.0

<!-- valheim-boosted:release-notes:start -->
**Full Changelog**: https://github.com/NekoShinobi/valheim-boosted/compare/0.1.0...0.2.0
<!-- valheim-boosted:release-notes:end -->

- Added default-on fresh replication relevance, bounded actor priority, early connection ZDO buffering with ordered replay, and negotiated 2 Hz map updates with marker interpolation.
- Dedicated servers now require public location sharing for all players by default, including unmodded clients. Added the independent `Map.ForceLocationSharing` switch; saved client preferences are preserved.
- Added replication/buffer/map activity and bandwidth counters to the HUD policy indicator, dashboard, history and report comparisons. History schema 4 and metric layout 3 retain earlier data and saved reports.

- Replaced the dashboard sidebar with shared Live, History and Reports navigation, focused live sections and the mod logo. Simplified page text, made report details/coverage expandable, and kept navigation available on mobile.
- Enabled send windows, Steam rate tuning, captain ownership and negotiated compression by default for new configs, preserving existing opt-outs and all compatibility/fallback checks.
- Added author metadata for Gale local ZIP imports and documented Linux local-icon troubleshooting. Packaging checks preserve the root icon and validate the optional author field.

## v0.1.0

**Pre-alpha · Initial package**

- Added draft-based GitHub release preparation with plain numeric tags such as `0.1.0`: write Markdown notes and choose release/pre-release in the release editor; preparation commits version files, changelog notes and README version updates to the target branch, builds that commit, then tags it and attaches both ZIPs with checksums. Publishing also triggers the versioned metrics image build.
- Clarified release preparation failures with the requested repository/tag, visible draft and published tags, recovery instructions, and a workflow summary; published pre-releases are distinguished from saved drafts.
- Fixed automated release commits to use GitHub's `CommittableBranch.branchName` input.
- Fixed draft attachment losing its tag when pinning the build commit; release updates now send the tag and target together and report exact identity mismatches.

- Added opt-in Stage 3 per-peer ZDO allowances with RTT/unsent-queue feedback and conservative connection-scoped Steam maximum-rate changes, including verified readback, restoration and external-writer handling.
- Added opt-in Stage 4 captain ownership for eligible vanilla ships, with replicated helm-grant/attachment checks, exact reviewed contracts, dwell/cooldown and bounded transfers.
- Added opt-in Stage 5 lossless ZDO compression with directional capability negotiation, per-session tokens, bounded framing/decompression, soft encoding budgets, vanilla send fallback and queued-frame draining.
- Added server-improvement views, per-peer settings, compression savings/cost, ship transfers, report metadata/comparisons and persistent LayerChart history metrics. Schema 3 appends metric layout 2 while retaining earlier samples, rollups and saved timelines.
- Added managed/Mono policy and protocol tests, real Harmony send transforms against client/dedicated references, and dashboard aggregation/history-migration checks for Stages 3–5.

- Added configurable empty-server idle mode: a 60-second grace period, a 10 FPS cap, five-second telemetry, and restoration on joins, saves, world changes and shutdown. Dashboard freshness follows the advertised sampling interval.

- Added seven-day persistent SQLite history, minute summaries for long ranges, aligned player/server timelines, shared chart inspection, and report timeline archives that survive live-history expiration.
- Track each login separately and recognize returning Steam accounts with a persistent server-keyed pseudonym. History combines players across reconnects by default, with session boundaries, weighted range totals and preserved archive weights; names and reused peer IDs never determine identity.
- Replace ECharts with LayerChart SVG graphs, coordinated hover, clickable time inspection, interactive legends and responsive dark styling.
- Migrate history storage to schema v2 without discarding raw samples; rebuild derived summaries to fix integer truncation in weighted means. Keep older unidentified history separate and older reports readable.
- Added default-enabled client performance sharing with bounded binary RPC batches, server-side clock alignment and uncertainty, frame/CPU/GC/connection metrics, F9 lag markers, congestion backoff, explicit sharing/reception switches and optional character-name labels. Unmodded clients remain compatible.
- Fixed dedicated-server Steam measurements failing with `Steamworks is not initialized` by selecting the queue query's Steam interface and checking that it matches the game's send path.

- Added persistent metrics reports, shared dashboard-stat reset, JSON downloads, and a separate baseline/candidate comparison page with settings, coverage, weighted timing means and observed counter rates. Added a writable reports volume to Docker deployment examples.
- Added the default-enabled Stage 2 fair scheduler for dedicated Steam servers: persistent peer cursor, capped 20 Hz credit, time/work limits, verified patch contracts, and vanilla fallback.
- Added replication attempts, outcomes, ZDO counts, payload bytes, service age/intervals, heartbeat age, bounded managed queue scans and growth, Valheim CPU/RSS/threads, frame p99/long-frame counts, save timings, and the running mod build ID.
- Added dashboard resource, scheduler and replication views with history and backward-compatible optional fields.

- Added the project signal-rune favicon to the metrics dashboard.

- Exposed underlying Steam query errors in snapshots and dashboard connection diagnostics, with degraded/recovered probe status and server exception logs limited to one warning every 30 seconds.

- Matched the default telemetry export path to the README: `/config/valheim-boosted/telemetry/snapshot.json`. Existing saved paths remain configurable.

- Added an in-game diagnostics HUD with an F8 toggle and configurable screen corner; defaults to the top right.
- Added local frame and network-update timings, per-peer transport metrics, queue estimates, traffic rates, and object ownership counts where available.
- Added atomic JSON snapshot export, enabled by default on servers and player hosts; optional on clients.
- Added a separate Svelte/Bun dashboard with connection charts, peer selection, and explicit waiting, stale, and stopped states.
- Added a metrics Docker image and GitHub build workflows.
- Added runtime game/protocol checks, reviewed client/server patch fingerprints, independent diagnostic switches, Harmony overlap detection, and probe status in the HUD/dashboard.
- Added Mono hook-lifecycle and game-contract checks; Steam telemetry follows the running game's status-query interface.
- Added Thunderstore packaging with BepInEx and Jötunn dependencies.
- Removed the extra ZIP wrapper from GitHub workflow downloads and documented Linux manual installation paths.
- Added a separate `-plugins.zip` download for direct extraction into `BepInEx/plugins`, including Docker's mounted plugins directory.

The scheduler is experimental and enabled by default in new configurations. Existing saved settings are preserved. Stages 3–5 are implemented behind default-off switches; general NPC simulation reassignment remains future work. Multiplayer performance and gameplay acceptance testing remain pending.
