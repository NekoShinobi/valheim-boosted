<p align="center"><img src="thunderstore/icon.png" width="112" height="112" alt="Valheim Boosted signal rune"></p>

<h1 align="center">valheim-boosted</h1>
<p align="center"><strong>See what your connection is doing.</strong><br>0.1.0 · Pre-alpha · Valheim networking diagnostics</p>

An in-game diagnostics HUD and a live server dashboard, built to help explain lag with measurements. This pre-alpha collects data; automatic tuning and simulation ownership changes are future work.

| Play with context | See the server |
| --- | --- |
| **F8 HUD** with a configurable corner | **Per-peer views** of latency, queues, and traffic |
| **Local timings** for frames and network updates | **Ownership counts** and bounded history charts |
| **Available transport metrics**, clearly labeled | **Freshness indicators** for missing or stale telemetry |

## Install the mod

Import `valheim-boosted-0.1.0.zip` into a Valheim profile in r2modman or Thunderstore Mod Manager, with **BepInExPack Valheim** and **Jötunn** installed. For manual installation, copy the ZIP's `BepInEx/plugins/ValheimBoosted` folder into your profile's `BepInEx/plugins/` directory.

Enter a world and press **F8**. The HUD starts at the top right; configure it in `BepInEx/config/valheim.boosted.cfg`. Install on the server for server metrics and on each client that wants its own HUD. Client CPU reporting to the server is not yet implemented.

[Installation & configuration](docs/MOD-INSTALL.md) · [Changelog](CHANGELOG.md) · [Metric definitions](docs/OBSERVABILITY.md)

## Start the metrics server

The **Svelte + TypeScript + ECharts** dashboard runs separately from Valheim, served by **Bun 1.4.0** on port **8080**. It reads the mod's JSON export; no connection to the game port is needed.

### Run with Bun

From the repository root:

```sh
bun install --frozen-lockfile
bun run build
HOST=127.0.0.1 PORT=8080 \
  TELEMETRY_PATH="$PWD/.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json" \
  HISTORY_SAMPLES=300 STALE_AFTER_MS=5000 \
  bun start
```

This example reads the development profile. For another server, set `TELEMETRY_PATH` to its snapshot path **as seen by the Bun process**. The page shows **Waiting** until the mod exports a snapshot.

### Run with Docker

For a Valheim container with `/srv/valheim/config:/config` mounted, set this in the mod's `valheim.boosted.cfg`, then restart Valheim:

```ini
[Telemetry]
Enabled = true
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

Share that export directory with the metrics container:

| Location | Path |
| --- | --- |
| Valheim container writes | `/config/valheim-boosted/telemetry/snapshot.json` |
| Docker host stores | `/srv/valheim/config/valheim-boosted/telemetry/snapshot.json` |
| Metrics container reads | `/telemetry/snapshot.json` |

Create the host directory first, with permissions that let Valheim write and the metrics image's `bun` user read it. Adjust the host path to match your existing Valheim volume. From the repository root:

```sh
docker build -f docker/metrics.Dockerfile -t valheim-boosted-metrics:local .
docker run -d --name valheim-boosted-metrics --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -e HOST=0.0.0.0 -e PORT=8080 \
  -e TELEMETRY_PATH=/telemetry/snapshot.json \
  -e HISTORY_SAMPLES=300 -e STALE_AFTER_MS=5000 \
  --mount type=bind,source=/srv/valheim/config/valheim-boosted/telemetry,target=/telemetry,readonly \
  valheim-boosted-metrics:local
```

Mount the **whole directory** read-only: the mod replaces `snapshot.json` atomically, so mounting only the file can leave the reader seeing an old snapshot. `HOST=0.0.0.0` lets Docker reach Bun inside the container; the port mapping exposes it only on the host's loopback interface.

Or use the included Compose file:

```sh
TELEMETRY_DIRECTORY=/srv/valheim/config/valheim-boosted/telemetry \
  docker compose -f docker/compose.metrics.yml up -d --build
```

`TELEMETRY_DIRECTORY` selects the **host mount source** for Compose; `TELEMETRY_PATH` selects the **snapshot file inside the metrics process**.

### Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `HOST` | `127.0.0.1`; `0.0.0.0` in Docker | HTTP bind address |
| `PORT` | `8080` | HTTP port; match Docker's container-port mapping if changed |
| `TELEMETRY_PATH` | Development-profile path above; `/telemetry/snapshot.json` in Docker | JSON snapshot to read |
| `HISTORY_SAMPLES` | `300` | Keep 1–3600 snapshots in memory; resets on restart or world/process change |
| `STALE_AFTER_MS` | `5000` | Minimum stale threshold, 1000–300000 ms; extended to three sample intervals |

Open [localhost:8080](http://127.0.0.1:8080) on the host. For an SSH host, forward port 8080 in VS Code's **Ports** panel. The service has no built-in authentication; use an access-controlled reverse proxy if exposing it beyond localhost.

[API details & deployment notes →](dashboard/README.md)

## Develop

```sh
python3 scripts/dev.py build
python3 scripts/dev.py deploy
```

The mod requires local Valheim references and BepInEx/Jötunn libraries; follow the [Linux + VS Code setup](docs/DEVELOPMENT.md). For UI development, run `bun run dev` and `bun run dev:ui` in separate terminals.

`mod/` holds the C# plugin, `dashboard/` the UI and Bun server, and `thunderstore/` the package metadata and artwork. GitHub workflows build the mod ZIP and a separate metrics image. [Build & release guide →](docs/CI.md)

**Pre-alpha status:** in-game validation and Docker execution remain pending. The Thunderstore package identifier is `valheim_boosted`; the project and mod name remain **valheim-boosted**.
