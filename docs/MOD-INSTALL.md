# Install valheim-boosted 0.1.0

**Pre-alpha.** Thunderstore identifier: `valheim_boosted`. Import the release ZIP into a Valheim mod-manager profile, or follow the manual steps below. The package declares both dependencies in its manifest.

Requires BepInExPack Valheim and Jötunn. This project builds with BepInExPack 5.4.2350 and Jötunn 2.30.0; confirm compatibility with your game build before use.

Extract the ZIP's BepInEx/plugins/ValheimBoosted directory into the corresponding BepInEx profile. The only binaries in the ZIP are our ValheimBoosted.dll and debug symbols. Install BepInEx/Jötunn separately.

For community-valheim-tools/valheim-server-docker, enable BEPINEX=true and put the ValheimBoosted plugin directory under /config/bepinex/plugins/. Put the Jötunn distribution there too.

## Upgrade from UrfMode

Close Valheim. Remove or archive UrfMode.dll and UrfMode.pdb outside BepInEx/plugins so both plugins do not run. Copy local.urfmode.cfg to valheim.boosted.cfg to retain settings if no new config exists. Update any old urfmode-telemetry export path and point the dashboard at the same location. scripts/dev.py deploy handles migration for the local development profile only.

Display name: valheim-boosted; assembly/namespace: ValheimBoosted; plugin GUID/config basename: valheim.boosted.

## Usage

Enter a world and press F8. The HUD defaults to HUD.Position = TopRight; TopLeft, BottomRight, and BottomLeft are also supported.

Server/host export is on by default. Client export is opt-in through Telemetry.ExportOnClient. Choose a shared export directory for the dashboard:

```ini
[Telemetry]
Enabled = true
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

The mod only observes metrics. It does not change ownership, tune networking, or send client CPU reports. The dashboard is a separate application/image.
