# Changelog

## v0.1.0

**Pre-alpha · Initial package**

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

The scheduler is experimental and enabled by default in new configurations. Existing saved settings are preserved. Native rate/window tuning, ownership reassignment, and client CPU reporting remain unimplemented. Multiplayer performance and gameplay acceptance testing remain pending.
