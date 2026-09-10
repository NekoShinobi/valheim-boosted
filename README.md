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

## Start the dashboard

The companion dashboard uses **Svelte + TypeScript + ECharts**, served by **Bun 1.4.0** on port **8080**.

```sh
bun install --frozen-lockfile
bun run build
bun start
```

Open [localhost:8080](http://127.0.0.1:8080). It reads the local development profile's snapshot by default; set `TELEMETRY_PATH` to read another server's export. Servers and player hosts export automatically. The page shows **Waiting** until a snapshot arrives.

For containers, run the separate metrics image with a read-only mount of the telemetry directory. [Dashboard & Docker setup →](dashboard/README.md)

## Develop

```sh
python3 scripts/dev.py build
python3 scripts/dev.py deploy
```

The mod requires local Valheim references and BepInEx/Jötunn libraries; follow the [Linux + VS Code setup](docs/DEVELOPMENT.md). For UI development, run `bun run dev` and `bun run dev:ui` in separate terminals.

`mod/` holds the C# plugin, `dashboard/` the UI and Bun server, and `thunderstore/` the package metadata and artwork. GitHub workflows build the mod ZIP and a separate metrics image. [Build & release guide →](docs/CI.md)

**Pre-alpha status:** in-game validation and Docker execution remain pending. The Thunderstore package identifier is `valheim_boosted`; the project and mod name remain **valheim-boosted**.
