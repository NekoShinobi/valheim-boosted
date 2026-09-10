# Changelog

## v0.1.0

**Pre-alpha · Initial package**

- Added an in-game diagnostics HUD with an F8 toggle and configurable screen corner; defaults to the top right.
- Added local frame and network-update timings, per-peer transport metrics, queue estimates, traffic rates, and object ownership counts where available.
- Added atomic JSON snapshot export, enabled by default on servers and player hosts; optional on clients.
- Added a separate Svelte/Bun dashboard with connection charts, peer selection, and explicit waiting, stale, and stopped states.
- Added a metrics Docker image and GitHub build workflows.
- Added runtime game/protocol checks, reviewed client/server patch fingerprints, independent diagnostic switches, Harmony overlap detection, and probe status in the HUD/dashboard.
- Added Mono hook-lifecycle and game-contract checks; Steam telemetry follows the running game's status-query interface.
- Added Thunderstore packaging with BepInEx and Jötunn dependencies.

This release collects diagnostics only. Automatic network tuning, ownership reassignment, and client CPU reporting to the server are not implemented. In-game validation of this release is pending.
