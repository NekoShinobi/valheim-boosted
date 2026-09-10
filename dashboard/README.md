# valheim-boosted metrics server

Svelte 5 + TypeScript + [LayerChart](https://www.layerchart.com/) SVG graphs, with a Bun HTTP server. Vite compiles Svelte; Bun manages packages, runs tests, runs Vite, and serves production files.

Run commands from the repository root: `bun install --frozen-lockfile`, `bun run build`, then `bun start` for port 8080. For hot reload, run `bun run dev` (API) and `bun run dev:ui` (UI, port 5173) in separate terminals.

## Configuration

| Environment variable | Default | Meaning |
| --- | --- | --- |
| HOST | 127.0.0.1 locally; 0.0.0.0 in Docker | Listener address |
| PORT | 8080 | TCP port |
| TELEMETRY_PATH | `.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json` locally; `/telemetry/snapshot.json` in Docker | Mod snapshot |
| HISTORY_SAMPLES | 300 | 1–3600 unique snapshots; cleared on world/process changes and service restart |
| REPORTS_DIRECTORY | `.local/reports` locally; `/reports` in Docker | Writable saved-report storage; independent of chart retention |
| HISTORY_RETENTION_DAYS | 7 | 1–365 days of detailed persistent samples; periodic expiration |
| HISTORY_DATABASE_PATH | `history.sqlite` within REPORTS_DIRECTORY | SQLite/WAL history, in the existing reports volume by default |
| STALE_AFTER_MS | 5000 | Minimum staleness threshold; extended to three sample windows for slower exporters |

The reader reopens the file every second, limits files to 2 MiB, validates schema v1, preserves null measurements, and retains only changed snapshots. It detects old timestamps and frozen sequences. Failed reads show the last good snapshot as stale. A stopped/menu snapshot is marked stopped.

`GET /api/metrics` returns status, last-read time, the last valid snapshot, bounded history, and `recording` window/coverage metadata. `GET /healthz` reports that HTTP is alive, independently of whether Valheim is running. Neither endpoint changes game state.

## Reports

The overview has **Save report**, **Reset stats** and **Compare reports** controls. `/reports` compares saved measurements and configuration context. JSON reports persist independently of `HISTORY_SAMPLES`; the active unsaved accumulator is memory-only. Recording pauses at a session or exported-settings change until reset. [Definitions, coverage and a repeatable test procedure](../docs/REPORTS.md).

| Endpoint | Behavior |
| --- | --- |
| `POST /api/reports` | Save the current window with JSON `{ "name": "Vanilla baseline" }`; returns the report with HTTP 201 |
| `POST /api/recording/reset` | JSON `{}` starts a new recording and clears live charts for all viewers; returns metrics |
| `GET /api/reports` | `{ reports: [...summaries], unreadable: number }`, newest first |
| `GET /api/reports/:id` | Read a saved report |
| `GET /api/reports/:id/download` | Download the same report as a JSON attachment |

Mutations require `application/json` and a body no larger than 4 KiB. Names are 1–100 characters. Browser origins must match the request host; TLS-terminating proxies must preserve the original `Host` header. No cross-origin browser access is enabled. These checks do not authenticate administrators; keep the existing access proxy in front of the service.

Saves use atomic file replacement, UUID filenames and a maximum of 1,000 reports (4 MiB each). At the limit, archive older JSON files from `REPORTS_DIRECTORY` before saving more. Saved report files are never automatically deleted. An empty recording returns 409; unavailable storage returns 503. Back up the reports volume as needed.

The overview shows per-peer RTT/queue histories, frame/ZDO-update timings, queue breakdowns, loaded ownership and client-reporting status. Reporting clients send frame/CPU/GC and connection summaries. Optional server-known character names label their timelines; no IPs, Steam IDs or hardware identifiers are included by this protocol. Overview browser polling is once per second and stops when the component is destroyed.

## Persistent history

`/history` overlays server, client and server-observed connection tracks on a shared time axis. It follows a recent window every ten seconds or accepts a fixed range. Returning Steam players are combined across logins by default; disable grouping to inspect one session. Session records and weighted range summaries distinguish login boundaries from missing measurements. Preserve the server's generated `ClientTelemetry.IdentityKey` to retain identity across restarts; older history without identity is never guessed from names. Select up to nine series, compare metrics and inspect a time or F9 marker. The history worker performs SQLite I/O, retention, aggregation and queries outside Bun's HTTP thread. It never accesses Unity objects.

| Endpoint | Behavior |
| --- | --- |
| `GET /api/history/status` | Retention, earliest/latest retained sample, database size and writer health |
| `GET /api/history/series?from=...&to=...` | Up to 256 series; `truncated` indicates more exist |
| `GET /api/history/series?from=...&to=...&group=player` | Group returning players by server-scoped identity and perspective |
| `GET /api/history/sessions?from=...&to=...&players=playerId,...` | Up to 256 login records for up to nine player IDs; `truncated` indicates more exist |
| `GET /api/history?from=...&to=...&series=id,id&metric=frameP95Ms&points=900` | Timestamped buckets, coverage, clock uncertainty and lag markers |
| `GET /api/history?from=...&to=...&players=playerId:client,playerId:connection&metric=frameMeanMs` | Weighted range summaries and buckets across all matching logins; may also include individual `series` IDs |

`from` and `to` are Unix milliseconds. The service bounds ranges to 366 days, selection to nine series and requested points to 10–1200 per series. Data older than configured retention is excluded even before physical cleanup. Minute summaries accelerate long ranges; recent minutes and partial boundaries use detailed samples. Saved reports include a separate bounded timeline, viewable at `/history?report=<report-id>`. [Storage schema and metric semantics](../docs/HISTORY.md).

**Diagnostics → Connection diagnostics** shows underlying measurement exceptions, their operation, and wrapper chain. A live snapshot can still contain failed measurements; the overview warns when a connected peer has unavailable data. Updated mod builds provide these details through the optional `measurementError` field; older snapshots show the status alone. Full exceptions are logged by the mod at most once every 30 seconds across peers. Runtime error messages may contain library names or local paths.

## Docker

```sh
docker build -f docker/metrics.Dockerfile -t valheim-boosted-metrics:local .
docker run --rm -p 127.0.0.1:8080:8080 \
  --mount type=bind,source=/absolute/host/telemetry,target=/telemetry,readonly \
  --mount type=volume,source=valheim-boosted-reports,target=/reports \
  valheim-boosted-metrics:local
```

The telemetry directory must exist and be readable by the image's bun user. Mount the **directory**, not just snapshot.json: the mod replaces the file atomically. Saved reports use a separate writable volume. Game saves, libraries and configuration are not part of the image. The Compose example retains a read-only root filesystem with only `/reports` and `/tmp` writable.

Alternatively:

```sh
TELEMETRY_DIRECTORY=/absolute/host/telemetry docker compose -f docker/compose.metrics.yml up -d --build
```

For the community Valheim image, set BEPINEX=true, install the plugin and Jötunn into `/config/bepinex/plugins`, and put this in `/config/bepinex/valheim.boosted.cfg` before starting the game:

```ini
[Telemetry]
Enabled = true
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

If its host volume is `/srv/valheim/config:/config`, use `/srv/valheim/config/valheim-boosted/telemetry` as the metrics container's host source. Create it with permissions matching the game writer and dashboard reader. The Compose example publishes 8080 only on host loopback. FRP can reach metrics:8080 when attached to the same Docker network. The service has no built-in authentication; use your existing access proxy for administrative access.

This Dockerfile produces a separate metrics image. An all-in-one derived Valheim image is not built by these workflows.
