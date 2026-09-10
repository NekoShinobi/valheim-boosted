# valheim-boosted

**See what your connection is doing.**

`0.2.0` · **Pre-alpha** · Valheim networking diagnostics

[Source & dashboard setup](https://github.com/NekoShinobi/valheim-boosted) · [Report an issue](https://github.com/NekoShinobi/valheim-boosted/issues)

An in-game view of connection quality, queues, and simulation timing, with server telemetry for a companion web dashboard.

## At a glance

| In game | On the server |
| --- | --- |
| F8 diagnostics HUD | Per-peer connection metrics |
| Latency, queue estimates, and traffic | Object ownership counts |
| Local frame and network-update timings | Atomic JSON snapshot export |

Available measurements depend on the transport and game state. Missing measurements are shown as unavailable.

## Install

Import this ZIP into a Valheim profile in r2modman or Thunderstore Mod Manager. Install the declared **BepInExPack Valheim** and **Jötunn** dependencies if prompted. Once published, the package can also be installed directly from its listing.

For manual installation, install those dependencies, then copy the ZIP's `BepInEx/plugins/ValheimBoosted` folder into your profile's `BepInEx/plugins/` directory.

Install on a client for its HUD, or on a server for server-side metrics. Server installation alone does not provide client frame/CPU measurements.

## In game

Launch once, enter a world, and press **F8** to show or hide the HUD. It starts in the **top-right** corner.

Settings live in `BepInEx/config/valheim.boosted.cfg`:

```ini
[HUD]
Enabled = true
Position = TopRight
ToggleKey = F8
```

Other positions: `TopLeft`, `BottomLeft`, and `BottomRight`.

## Server telemetry

Servers and player hosts export to `/config/valheim-boosted/telemetry/snapshot.json` by default, matching the documented container setup. Existing configs retain their saved path; update it and restart to use this location. For a non-container installation, choose a writable absolute path instead:

```ini
[Telemetry]
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

The optional **Svelte/Bun dashboard** runs separately on port **8080** and reads this file. It is distributed separately from this mod ZIP. Local client export is opt-in through `ExportOnClient = true`.

## Pre-alpha scope

Supported diagnostic contracts: **Valheim 1.0.7 / protocol 39**. Unsupported versions or changed hook targets disable affected probes and report why. Other players are not required to install this mod.

Individual probes can be disabled in the config's `[Features]` section: `FrameTiming`, `GameCounters`, `NetworkTiming`, `ZdoReceive`, `SteamTransport`, and `Ownership`. Restart after changing these switches. Set `[Telemetry] Enabled = false` to turn off all collection and hooks.

The HUD shows compatibility and hook status; the dashboard's **Diagnostics** section includes every probe, failure reasons, and hook invocation counts.

Steam query failures include the underlying exception and operation in the snapshot. The dashboard flags affected connections, while the server logs the full exception at most once every 30 seconds across peers. A degraded Steam probe keeps retrying and reports recovery automatically; live snapshots alone do not imply successful connection measurements.

The Stage 2 fair scheduler is enabled by default for dedicated Steam servers in new configurations. Existing configs retain their saved setting; set `[Scheduling] Enabled = false` and restart to use vanilla scheduling; defaults are `MaxCallsPerFrame = 4`, `TimeBudgetMs = 2`, and `MaxDebtPerPeer = 2`. It targets 20 Hz opportunities while preserving vanilla packets and queue limits. A single send can exceed the soft time budget. Multiplayer acceptance testing remains pending. [Scheduler details](https://github.com/NekoShinobi/valheim-boosted/blob/main/docs/SCHEDULING.md).

Additional default-on probes are `Replication`, `ConnectionHealth`, and `ProcessResources`. They expose send attempts, service age, heartbeat age, managed queue growth, process CPU/RSS, frame tails and save timings. Opt-in Stages 3–5 add per-peer ZDO allowances, connection maximum-rate tuning, ship captain ownership and negotiated lossless compression. All four new switches default off and require a restart. Stages 3–4 apply on dedicated Steam servers; compression needs both endpoints enabled and agreed. The dashboard shows their settings, savings/cost and history. [Configuration and limits](https://github.com/NekoShinobi/valheim-boosted/blob/main/docs/SERVER-IMPROVEMENTS.md). General NPC ownership rebalancing remains future work.

## Player telemetry and history

With the mod installed on both server and clients, performance summaries are shared by default using bounded game RPCs. The dashboard's **History** page aligns client FPS/frame intervals, CPU/GC, lag markers and both sides' connection measurements with server timing. Default history retention is seven days, configurable on the metrics server. **F9** marks an incident when the client processes the key.

Set `[ClientTelemetry] ShareWithServer = false` on a client to stop sharing, or `ReceiveFromClients = false` on a server to decline reports. The server can set `IncludePlayerNames = false` to use opaque session labels. These settings require a restart. Unmodded clients remain compatible. Sharing is independent of local JSON export and needs no additional client-facing port.

Upgrading from the earlier local UrfMode prototype? Remove its DLL before installing. To retain settings, copy `local.urfmode.cfg` to `valheim.boosted.cfg` if the new config does not exist, and update any old export path.
