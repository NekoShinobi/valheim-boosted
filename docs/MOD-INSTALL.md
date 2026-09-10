# Install valheim-boosted 0.1.0

**Pre-alpha.** Thunderstore identifier: `valheim_boosted`. Import the release ZIP into a Valheim mod-manager profile, or follow the manual steps below. The package declares both dependencies in its manifest.

Requires BepInExPack Valheim and Jötunn. This project builds with BepInExPack 5.4.2350 and Jötunn 2.30.0; confirm compatibility with your game build before use.

## Linux manual installation

Download **`valheim-boosted-0.1.0-plugins.zip`** for direct installation into an existing `BepInEx/plugins` directory. Install BepInEx/Jötunn separately and stop the server or close the game before updating:

```sh
unzip -o valheim-boosted-0.1.0-plugins.zip -d /path/to/BepInEx/plugins
```

The ZIP contains `ValheimBoosted/ValheimBoosted.dll` and `ValheimBoosted/ValheimBoosted.pdb`, plus `ValheimBoosted/build-references.json` in CI builds. Extracting it places everything in the plugin's own folder. `-o` replaces the existing plugin files when upgrading; it does not change your configuration. This ZIP is for manual installation; use the regular package below for Thunderstore.

For community-valheim-tools/valheim-server-docker, enable `BEPINEX=true` and extract into the `bepinex/plugins` directory under the host directory mounted at `/config`:

```sh
unzip -o valheim-boosted-0.1.0-plugins.zip -d /path/to/host/config/bepinex/plugins
```

This corresponds to `/config/bepinex/plugins` inside the container. Install Jötunn there too. The container's `bepinex` path is lowercase.

## Thunderstore package

The regular package follows the [Thunderstore BepInEx packaging rules](https://wiki.thunderstore.io/mods/packaging-your-mods) and [Valheim install routes](https://github.com/thunderstore-io/ecosystem-schema/blob/master/games/data/generated/valheim.yml):

```text
valheim-boosted-0.1.0.zip
├── manifest.json
├── icon.png
├── README.md
├── CHANGELOG.md
├── build-references.json                 # CI builds only
└── BepInEx/plugins/ValheimBoosted/
    ├── ValheimBoosted.dll
    └── ValheimBoosted.pdb
```

If you already downloaded this package and want to install it manually, extract its plugin files into the directory **containing** `BepInEx`:

```sh
profile_root="/path/to/valheim-or-mod-manager-profile"
unzip -l valheim-boosted-0.1.0.zip
unzip -o valheim-boosted-0.1.0.zip 'BepInEx/plugins/ValheimBoosted/*' -d "$profile_root"
```

This puts the DLL at `$profile_root/BepInEx/plugins/ValheimBoosted/ValheimBoosted.dll`. Do not set the destination to `BepInEx/plugins`: the ZIP already includes that prefix. `-o` replaces the existing plugin files when upgrading; it does not change your configuration.

Older GitHub Actions downloads named `valheim-boosted-mod.zip` wrap the package in another ZIP. Extract that outer archive into a temporary folder first, then use the inner `valheim-boosted-0.1.0.zip` with the commands above. New workflow runs upload both packages as individual artifacts without an extra wrapper. BepInEx itself does not extract ZIP files.

## Upgrade from UrfMode

Close Valheim. Remove or archive UrfMode.dll and UrfMode.pdb outside BepInEx/plugins so both plugins do not run. Copy local.urfmode.cfg to valheim.boosted.cfg to retain settings if no new config exists. Update any old urfmode-telemetry export path and point the dashboard at the same location. scripts/dev.py deploy handles migration for the local development profile only.

Display name: valheim-boosted; assembly/namespace: ValheimBoosted; plugin GUID/config basename: valheim.boosted.

## Usage

Enter a world and press F8. The HUD defaults to HUD.Position = TopRight; TopLeft, BottomRight, and BottomLeft are also supported.

Server/host export is on by default. Client export is opt-in through Telemetry.ExportOnClient. The default export path matches the README's container setup:

```ini
[Telemetry]
Enabled = true
ExportOnServer = true
ExportPath = /config/valheim-boosted/telemetry/snapshot.json
```

Existing configs keep their saved `ExportPath`; edit it and restart to adopt this default. For a non-container installation, set a writable absolute path and point the dashboard at the same file. In community-valheim-tools/valheim-server-docker, the mod config is `/config/bepinex/valheim.boosted.cfg`.

By default the mod collects metrics and enables Stage 2 fair replication scheduling for dedicated Steam servers; see [scheduler configuration](SCHEDULING.md). Installed clients also share bounded performance summaries, including frame intervals, CPU and GC, with compatible servers. See [client telemetry and history](HISTORY.md) for sharing switches, F9 lag markers and seven-day retention. The dashboard is a separate application/image.
