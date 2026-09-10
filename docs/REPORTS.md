# Saved reports and comparisons

Reports capture the metrics service's observed recording window. They summarize fresh, unique snapshots, independently of the rolling chart history. They do not contain a replay of the game or raw frame samples.

## Capture a baseline and candidate

1. Start the game and let the world warm up. Open the dashboard and check that the relevant probes and peer measurements are available.
2. Select **Reset stats**. The next complete exported window starts the recording; a window straddling the reset is skipped.
3. Run a repeatable scenario. Keep the save, location, player count, activity and duration comparable. Name it (for example, `Vanilla · base combat · 5 players`) and select **Save report**.
4. Apply one networking change. For a fair-scheduler baseline, set `[Scheduling] Enabled = false` and restart Valheim; enable it and restart for the candidate.
5. After warm-up, reset stats, repeat the scenario, and save the candidate. Open **Compare reports** and select both recordings.

Saving copies the current aggregates to disk and continues recording. Reset clears the unsaved aggregates and live chart history for everyone connected to that metrics service. Instantaneous overview cards still show the latest snapshot. It never changes the game's counters, configuration or scheduling, and never deletes a saved report.

The first fresh running snapshot starts automatic recording after metrics-service startup. Old files, duplicates, menu/stopped snapshots and partial windows at reset do not create report samples. A process/world, role, build, compatibility, feature-switch or scheduler-setting change pauses an existing recording. Save that window if wanted, then reset to record the current session. Temporary snapshot outages do not reset the recording; missed sequences and collection gaps remain visible. Unsaved recordings are lost on metrics-service restart.

## What the numbers mean

- **Timing means** are weighted by exported event counts (frames, network updates or service intervals).
- **Other means** average available observations. Peer metrics pool connected-peer observations; a player present longer contributes more. Reports do not match individual players across sessions.
- **Worst-window p95/p99** is the highest exported percentile among recorded windows. It is not a whole-recording percentile. The mod's percentile sample buffer is bounded, and raw samples cannot be reconstructed from JSON summaries.
- **Counter rates** sum per-window counters and divide by observed seconds in windows where that metric was available. The table also shows the observed total. Peer counters sum available connected peers. Missing peers/probes may undercount; inspect coverage and transport availability.
- **Coverage** includes unique snapshots, observed sample-window duration, elapsed capture time, missed sequences, and per-metric window/observation counts. Polling can miss overwritten snapshots; missing intervals are not interpolated or treated as zero.
- **Peaks** and worst-window percentiles tend to grow with longer recordings. They should be compared over similar durations.

Frame interval includes frame limiting and waits. CPU uses 100% for one core. Replication service age measures send-opportunity age, not NPC position error. A decrease in bandwidth or CPU does not on its own prove better gameplay. This view cannot establish causality or measure client frame performance and player-perceived desync that the mod does not export.

Each report records mod version/build ID, game version/protocol/module, process and world session IDs, configured feature switches, scheduler settings, and the number of windows where the scheduler reported active. Session IDs cannot identify a persistent save or guarantee equal workload; put scenario details in the report name. Comparisons show numeric and percentage deltas without automatically declaring a winner. A zero baseline has no percentage delta; missing measurements remain unavailable.

## Storage

`REPORTS_DIRECTORY` defaults to `.local/reports` when running Bun locally and `/reports` in Docker. Mount `/reports` as a writable persistent volume. The supplied Compose file includes one; `docker compose down -v` deletes named volumes, including reports.

Saved reports are versioned JSON (`reportVersion: 1`) written atomically under UUID filenames. Download them from the comparison page, back up the directory, or archive older JSON files when reaching the 1,000-report limit. Saving and resetting never delete existing reports. The UI reports unreadable files rather than treating them as valid data. Importing/uploading external reports is not implemented; comparisons use reports stored on this metrics service.

No C# mod update is required for these controls. Older mod builds can produce reports, with absent Stage 2 fields and build IDs shown as unavailable. The dashboard's report JSON format is separate from the mod's telemetry schema v1.
