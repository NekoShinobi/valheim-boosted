# valheim-boosted metrics server

Svelte 5 + TypeScript + Apache ECharts frontend, with a Bun HTTP server. Vite compiles Svelte; Bun manages packages, runs tests, runs Vite, and serves production files.

Run commands from the repository root: `bun install --frozen-lockfile`, `bun run build`, then `bun start` for port 8080. For hot reload, run `bun run dev` (API) and `bun run dev:ui` (UI, port 5173) in separate terminals.

## Configuration

| Environment variable | Default | Meaning |
| --- | --- | --- |
| HOST | 127.0.0.1 locally; 0.0.0.0 in Docker | Listener address |
| PORT | 8080 | TCP port |
| TELEMETRY_PATH | `.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json` locally; `/telemetry/snapshot.json` in Docker | Mod snapshot |
| HISTORY_SAMPLES | 300 | 1–3600 unique snapshots; cleared on world/process changes and service restart |
| STALE_AFTER_MS | 5000 | Minimum staleness threshold; extended to three sample windows for slower exporters |

The reader reopens the file every second, limits files to 2 MiB, validates schema v1, preserves null measurements, and retains only changed snapshots. It detects old timestamps and frozen sequences. Failed reads show the last good snapshot as stale. A stopped/menu snapshot is marked stopped.

`GET /api/metrics` returns status, last-read time, the last valid snapshot, and bounded history. `GET /healthz` reports that HTTP is alive, independently of whether Valheim is running. Neither endpoint changes game state.

The UI shows per-peer RTT/queue histories, frame/ZDO-update timings, queue breakdowns, and loaded ownership. Up to eight peers are charted together; selecting a row focuses one peer. Player names, IPs and Steam IDs are not exported. Remote client CPU data is unavailable. Browser polling is once per second and stops when the component is destroyed.

## Docker

```sh
docker build -f docker/metrics.Dockerfile -t valheim-boosted-metrics:local .
docker run --rm -p 127.0.0.1:8080:8080 --mount type=bind,source=/absolute/host/telemetry,target=/telemetry,readonly valheim-boosted-metrics:local
```

The directory must exist and be readable by the image's bun user. Mount the **directory**, not just snapshot.json: the mod replaces the file atomically. Only telemetry needs sharing; game saves, libraries and configuration are not part of the image.

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
