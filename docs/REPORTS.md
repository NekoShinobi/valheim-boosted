# Saved reports and comparisons

Reports capture the metrics service's observed recording window. They summarize fresh, unique snapshots, independently of the rolling chart history. They do not contain a replay of the game or raw frame samples.

## Capture a baseline and candidate

1. Start the game and let the world warm up. Open **Live → Diagnostics** and **Live → Connections** to check probe and peer measurements.
2. In **Live → Overview**, expand **Save a report** and select **Reset stats**. The next complete exported window starts the recording; a window straddling the reset is skipped.
3. Run a repeatable scenario. Keep the save, location, player count, activity and duration comparable. Name it (for example, `Vanilla · base combat · 5 players`) and select **Save report**.
4. Apply one networking change. For a fair-scheduler baseline, set `[Scheduling] Enabled = false` and restart Valheim; enable it and restart for the candidate.
5. After warm-up, reset stats, repeat the scenario, and save the candidate. Open **Reports** and select both recordings.

Saving copies the current aggregates to disk and continues recording. Reset clears the unsaved aggregates and live chart history for everyone connected to that metrics service. Instantaneous overview cards still show the latest snapshot. It never changes the game's counters, configuration or scheduling, and never deletes a saved report.

The first fresh running snapshot starts automatic recording after metrics-service startup. Old files, duplicates, menu/stopped snapshots and partial windows at reset do not create report samples. A process/world, role, build, compatibility, feature-switch or scheduler-setting change pauses an existing recording. Save that window if wanted, then reset to record the current session. Temporary snapshot outages do not reset the recording; missed sequences and collection gaps remain visible. Unsaved recordings are lost on metrics-service restart.

## What the numbers mean

- **Timing means** are weighted by exported event counts (frames, network updates or service intervals).
- **Other means** average available observations. The comparison page's peer metrics pool connected-peer observations; a player present longer contributes more. Use **View timeline** for individual players, including returning-account grouping and separate login records.
- **Worst-window p95/p99** is the highest exported percentile among recorded windows. It is not a whole-recording percentile. The mod's percentile sample buffer is bounded, and raw samples cannot be reconstructed from JSON summaries.
- **Counter rates** sum per-window counters and divide by observed seconds in windows where that metric was available. Enable **Show coverage** to see the observed total. Peer counters sum available connected peers. Missing peers/probes may undercount; inspect coverage and transport availability.
- **Coverage** includes unique snapshots, observed sample-window duration, elapsed capture time, missed sequences, and per-metric window/observation counts. Polling can miss overwritten snapshots; missing intervals are not interpolated or treated as zero.
- **Peaks** and worst-window percentiles tend to grow with longer recordings. They should be compared over similar durations.

Frame interval includes frame limiting and waits. CPU uses 100% for one core. Replication service age measures send-opportunity age, not NPC position error. A decrease in bandwidth or CPU does not on its own prove better gameplay. Client frame/CPU measurements require reporting clients and are available in the saved timeline; the summary comparison table continues to describe server-observed measurements. Neither view establishes causality or measures object-specific desync.

Each report records mod version/build ID, game version/protocol/module, process and world session IDs, configured feature switches, scheduler settings, and the number of windows where the scheduler reported active. Session IDs cannot identify a persistent save or guarantee equal workload; put scenario details in the report name. Comparisons show numeric and percentage deltas without automatically declaring a winner. A zero baseline has no percentage delta; missing measurements remain unavailable.

## Storage

`REPORTS_DIRECTORY` defaults to `.local/reports` when running Bun locally and `/reports` in Docker. Mount `/reports` as a writable persistent volume. The supplied Compose file includes one; `docker compose down -v` deletes named volumes, including reports.

Saved reports are versioned JSON (`reportVersion: 1`) written atomically under UUID filenames. Download them from the comparison page, back up the directory, or archive older JSON files when reaching the 1,000-report limit. Saving and resetting never delete existing reports. The UI reports unreadable files rather than treating them as valid data. Importing/uploading external reports is not implemented; comparisons use reports stored on this metrics service.

The report controls work with older mod builds. Collecting remote client performance requires the updated mod on both the server and participating clients. The dashboard's report JSON format is separate from the mod's telemetry schema v1.

## Saved timelines

Saving a report also archives the retained history for its server process/world and recording range. **Open timeline** compares client, server and server-connection tracks. An archive retains up to 64 series and approximately 2,400 buckets in total (at most 2,600 rows). Its resolution is explicit, and omitted series are flagged. Each bucket preserves weighted timing means, FPS by observed duration, maxima/worst-window percentiles, counter totals, coverage and maximum clock uncertainty. Only the first lag marker per archived bucket is retained; a worst-frame timestamp is retained only for a single original sample.

New timelines retain login session records, server-scoped player IDs and per-metric weights. Returning players can be combined within the recording without averaging unequal login averages or counting offline time. This does not turn pooled metrics on the comparison page into player-specific comparisons. Older archives without IDs remain individual series; unavailable weights leave some whole-range means blank. The saved timeline remains scoped to one server process/world; use live History to inspect a player across server restarts.

An archive is stored inside the report JSON and survives seven-day live-history expiration. It captures data already received when you save; delayed client windows that arrive later do not change the saved report. Data already expired or never collected cannot be recovered by saving. When the history worker cannot archive data, the report records a timeline error and still saves its existing aggregates. Existing reports without a timeline remain readable. Report files are limited to 4 MiB; detailed live history is independently retained in SQLite. **Reset stats** does not erase this persistent history.

Reports also record Stage 3–5 settings and measurements: ZDO allowances, effective Steam maximum rates, largest connection failure total, operation-weighted compression cost, bytes saved and observed send/receive/reject/skip/ship-transfer rates. Savings include compression envelopes and exclude Steam overhead. A settings change pauses recording even when the mod version remains 0.1.0. Older reports omit these metrics and remain readable. See [server improvements](SERVER-IMPROVEMENTS.md).

In **Live → Overview**, expand **Save a report** for naming, saving and resetting the recording. On **Reports**, expand **Recording details & settings** for build and configuration metadata; enable **Show coverage** for per-metric sample counts and observed totals.
