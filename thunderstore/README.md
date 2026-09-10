# valheim-boosted

**See what your connection is doing.**

`0.1.0` · **Pre-alpha** · Valheim networking diagnostics

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

Servers and player hosts export to `BepInEx/valheim-boosted-telemetry/snapshot.json` by default. To choose a shared location, edit the config and restart:

```ini
[Telemetry]
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

The optional **Svelte/Bun dashboard** runs separately on port **8080** and reads this file. It is distributed separately from this mod ZIP. Local client export is opt-in through `ExportOnClient = true`.

## Pre-alpha scope

This release measures networking; it does not automatically improve performance or tune simulation ownership, queues, or send rates. Remote client CPU reporting is not implemented. In-game validation of this release is pending.

Upgrading from the earlier local UrfMode prototype? Remove its DLL before installing. To retain settings, copy `local.urfmode.cfg` to `valheim.boosted.cfg` if the new config does not exist, and update any old export path.
