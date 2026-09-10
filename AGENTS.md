# valheim-boosted: agent context

This file applies to the whole repository. Keep it aligned with the code and public documentation as the project evolves. Local references and research described below may be absent on a fresh checkout.

## Working conventions

- Development targets Linux and VS Code, including Remote SSH; Visual Studio is not required.
- In the owner's workspace, read `/home/user/.codex/RTK.md` when present and prefix shell commands with `rtk`. The examples below use `rtk proxy` to preserve command behavior. On another machine without RTK, the underlying commands work directly.
- Keep `docs/` for durable installation, configuration, usage, troubleshooting and contributor instructions. Do not put task reports, play-session reviews, installation inventories, test-run summaries or planning notes there. Report routine completion and validation in chat; create a separate report only when requested, under ignored `.local/reports/` for private material.
- Inspect `git status` before editing. Preserve unrelated work, including untracked source files from an ongoing feature. Ignored directories can contain valuable game references, profiles, research and reports; do not delete them as routine cleanup.
- Resolve paths from the repository root. The workspace was originally named `valheim-modding`, and older paths or symlinks may still use that name. The project and repository are now `valheim-boosted`; do not hardcode an owner's absolute checkout path into source or committed editor settings.
- Prefer existing local tools. If installing or updating software is necessary, report what changed on the machine, distinguishing project-local downloads from system installations. Do not claim a deployment or live-game test when only a build or fixture test ran.

## Purpose and architecture

The target deployment is one Steam dedicated server with multiple clients of varying network and computer quality. Valheim distributes simulation ownership: hosting the dedicated server does not mean it simulates every nearby object. A slow owner can affect other players. The project first measures these conditions and supports targeted networking improvements; a send-scheduling change alone does not solve simulation ownership.

The current pre-alpha combines:

- A C# BepInEx/Jötunn mod: diagnostics HUD, server/peer metrics, optional client performance RPCs, an experimental fair replication scheduler and default-on Stage 3–5 improvements.
- A separate Bun service with a Svelte/TypeScript/LayerChart dashboard: JSON snapshot ingestion, persistent SQLite history, reports and comparisons.
- Two release outputs: mod installation ZIPs and a metrics Docker image. The mod does not launch Bun or host an HTTP listener inside Unity.

GitHub releases use the native draft editor for Markdown notes and the pre-release checkbox. For a new tag, `.github/workflows/release.yml` uses `scripts/prepare-release.py` and `scripts/release-version.py` to commit the four source version fields, changelog notes and README version updates to the draft's target branch before calling the reusable mod build. The commit API checks the expected branch head; branch protection remains effective. Only a successful build gets a tag and the original ZIPs plus checksums attached. Existing tags are rebuilt unchanged. It leaves publication to the editor; publishing triggers the metrics image build. Git tags use plain `Major.Minor.Patch` (for example `0.1.0`), without a `v` prefix or pre-release suffix. Changelog headings retain their separate `## vMajor.Minor.Patch` format; preserve generated-note markers and older hand-written history. See [release steps](docs/CI.md#create-a-github-release-or-pre-release).

Names are intentional: display/repository `valheim-boosted`, assembly/namespace `ValheimBoosted`, BepInEx GUID and config basename `valheim.boosted`, Thunderstore identifier `valheim_boosted`. `UrfMode`/`urfmode` is the predecessor name, retained only where migration or historical references require it.

## Source map

| Location | Responsibility |
| --- | --- |
| `mod/Plugin.cs` | BepInEx lifecycle, configuration, HUD and integration wiring |
| `mod/Telemetry*`, `SteamMetrics.cs`, `ResourceSampler.cs` | Collection, probes, compatibility and atomic export |
| `mod/Replication*`, `FairScheduler.cs` | Reviewed game contracts and bounded fair send scheduling |
| `mod/AdvancedReplication.cs`, `SendWindowPolicy.cs`, `SteamRate*`, `Captain*`, `ZdoCompression.cs`, `CompressionSession.cs`, `LosslessZdoCodec.cs` | Default-on Stages 3–5; see `docs/SERVER-IMPROVEMENTS.md` |
| `mod/ReplicationRelevance.cs`, `EarlyZdoIntegration.cs`, `MapSharingIntegration.cs`, `NetworkEnhancement*`, `MapPositionProtocol.cs` | Default-on relevance, actor priority, bounded connection buffering, negotiated map markers and forced sharing; see `docs/SERVER-IMPROVEMENTS.md` |
| `mod/IdleServer*` | Empty dedicated-server frame cap, grace period, restoration and sampling cadence; see `docs/IDLE-SERVER.md` |
| `mod/ClientTelemetry*` | Optional client summaries, binary protocol, peer lifecycle and clock alignment |
| `mod/PlayerIdentity.cs` | Server-keyed Steam account pseudonyms; never export the key or raw account ID |
| `dashboard/shared/` | Validated telemetry, report and history contracts shared with the frontend |
| `dashboard/server/` | Bun HTTP endpoints, snapshot reader, recording, report files and SQLite worker |
| `dashboard/client/` | Shared `DashboardShell.svelte` navigation, Svelte views and LayerChart SVG charts |
| `docker/` | Metrics image and companion Compose service |
| `scripts/`, `tests/`, `.github/workflows/` | Local setup, validation, reference preparation, builds and packaging |
| `thunderstore/` | Manifest (including optional `author` for Gale local imports), package README and icon; root `CHANGELOG.md` is packaged too |

Start with [README.md](README.md). Detailed behavior belongs in [installation](docs/MOD-INSTALL.md), [observability](docs/OBSERVABILITY.md), [compatibility](docs/COMPATIBILITY.md), [scheduling](docs/SCHEDULING.md), [history](docs/HISTORY.md) and [reports](docs/REPORTS.md), rather than expanding this file into a second specification.

## Baseline game server and container paths

Our baseline is [community-valheim-tools/valheim-server-docker](https://github.com/community-valheim-tools/valheim-server-docker), image `ghcr.io/community-valheim-tools/valheim-server`. Use its BepInEx mode (`BEPINEX=true`); `VALHEIM_PLUS=true` is a different, mutually exclusive upstream mode. Jötunn and our plugin must also be installed.

The upstream image persists configuration at `/config` and downloaded server files at `/opt/valheim`. Its writable plugin input is `/config/bepinex/plugins`; upstream copies plugins into its runtime installation. Target the persistent input for deployment. See the [upstream BepInEx documentation](https://github.com/community-valheim-tools/valheim-server-docker#bepinexpack-valheim) when changing integration assumptions.

| Artifact | Game container | Metrics container |
| --- | --- | --- |
| Mod DLL | `/config/bepinex/plugins/ValheimBoosted/ValheimBoosted.dll` | Not needed |
| Mod configuration | `/config/bepinex/valheim.boosted.cfg` | Not needed |
| Exported snapshot | `/config/valheim-boosted/telemetry/snapshot.json` | `/telemetry/snapshot.json` via a read-only directory mount |
| Saved reports and history | Not needed | Writable `/reports`; database defaults to `/reports/history.sqlite` |

These are paths inside the respective containers. A host directory such as `/srv/valheim/config` is an example, not a known production mount. Container `bepinex` is lowercase; a normal local profile uses `BepInEx/config/` and `BepInEx/plugins/`.

The mod's `Telemetry.ExportPath` and Bun's `TELEMETRY_PATH` must refer to the same underlying file through their own filesystem views. Mount the telemetry directory, since export uses atomic file replacement. Existing BepInEx configs preserve saved values when defaults change, and the plugin must load successfully before its config is generated.

The metrics service defaults to TCP 8080, with `HOST=127.0.0.1` locally and `0.0.0.0` inside its image. The Compose example publishes to host loopback. Its report/history APIs have no administrator authentication built in; preserve the deployment's access proxy. Game UDP ports, host port mappings, frp/Cloudflare routing, production credentials and real world volumes are deployment state outside this repository. Inspect the relevant deployment when needed; do not infer it from an old URL or assume this checkout is the running server.

`docker/compose.metrics.yml` describes the metrics companion, not a complete game-server stack. `docker/metrics.Dockerfile` needs neither SteamCMD nor game DLLs. SteamCMD belongs to game provisioning and the mod-build reference workflow (`scripts/install-server-references.sh`, dedicated-server app 896660). Build/test work and replacing plugins in a running deployment are separate actions; use the task's existing deployment authorization and stop the affected game process before replacing its plugin files.

## Files outside the committed project

`.gitignore` is the authoritative exclusion list. These paths are useful inputs or local state, not missing repository content:

| Local path | Meaning and handling |
| --- | --- |
| `.local/environment.json` | Discovered Steam client installation; `VALHEIM_INSTALL` overrides it. The game is normally under a Steam library's `steamapps/common/Valheim`, potentially in another library. |
| `.local/references/Managed/` | Private snapshot of game/Unity/Steamworks DLLs used to compile. Local setup normally copies the installed client; CI preparation copies dedicated-server references. Establish which one is present before reasoning about behavior. |
| `.local/references/ModLibraries/` | Pinned BepInEx, Jötunn and Harmony runtime dependencies. Integration runners also need MonoMod.RuntimeDetour, MonoMod.Utils and Mono.Cecil; copying only `0Harmony.dll` is insufficient. |
| `.local/references/manifest.json` | Local source installation, Steam build ID and assembly hashes. |
| `.local/references/ci-provenance.json` | CI/server-reference provenance when generated; packaging can include this metadata, never the game DLLs. |
| `.local/ci-server/valheim_server_Data/Managed/` | Optional independently downloaded dedicated-server assemblies for comparison. This directory is not evidence of a running server. |
| `decomp/` | ILSpy-generated C# reference and its input manifest. Not compiled, edited by hand, committed or shipped. |
| `.local/profile/` | Isolated local BepInEx/Jötunn development profile, deployed Debug plugin, config, logs and optional telemetry. Separate from production. |
| `.local/reports/` | Default Bun report JSON and SQLite/WAL files when running from the repository root. Preserve these as runtime data. |
| `.local/migrations/` | Archived predecessor plugin files; keep legacy DLLs outside active plugins to prevent double loading. |
| `.tools/` | Project SDK, NuGet caches, downloaded mod packages and optional Mono/SteamCMD/editor tools. Availability differs by machine; inspect before downloading again. |
| `research/valheim-networking/REPORT.md` and `source-manifest.json` | Local investigation, source revisions, hashes and reasoning. Historical research, not an authoritative implementation checklist or proof of live performance. |
| `INSTALLATION-REPORT.md` | Legacy local installation record; excluded from publication. New private reports, when requested, belong under `.local/reports/`. |
| `artifacts/`, `**/bin/`, `**/obj/`, `node_modules/`, `dist/` | Generated packages, screenshots, build output and dependencies. |
| `.vscode/settings.json`, `.env*`, `*.log` | Machine/runtime settings and logs. Share settings through `settings.example.json` or an explicit `.env.example`, without real credentials. |

Do not force-add excluded material to make a fresh clone look like this workspace. Put durable explanations in `docs/` or this file and regenerate private build inputs through the scripts. Preserve actual configs, worlds and reports; a setup script is not a production migration tool.

## Decompiled references and game updates

The local `decomp/assembly_valheim/` contains the main networking code. Useful entry points are `ZNet.cs`/`ZNetPeer.cs` for connections, `ZRpc.cs`/`ZRoutedRpc.cs` for RPCs, `ZDOMan.cs`/`ZDO.cs` for replication/state, `ZNetScene.cs`/`ZNetView.cs` for loaded objects and ownership, and `ZSteamSocket.cs`/`ZPlayFabSocket.cs` for transports. `Assembly-CSharp` and the other assembly directories are separate reference outputs.

Normal `rg` searches skip these ignored paths. Search them deliberately and narrowly:

```sh
rtk proxy rg --no-ignore -n 'SendZDOToPeers2|SendZDOs' decomp/assembly_valheim/ZDOMan.cs
rtk proxy python3 scripts/dev.py doctor
```

`doctor` checks the installed client against `.local/references/manifest.json` and compares that snapshot with `decomp/manifest.json`. After an intended game update, use `dev.py refresh` followed by `dev.py decompile`. Refresh changes build inputs; decompile replaces generated source directories. Do not refresh references halfway through investigating a reproducible build mismatch without preserving its provenance.

The reviewed game baseline is currently Valheim **1.0.7**, protocol **39**; the existing local client snapshot records Steam build **25185596**. Recheck the manifests and `CompatibilityPolicy` in `mod/IntegrationState.cs` instead of treating those numbers as permanently current. Read-only decompiled C# is useful evidence, but exact patch assertions use the actual assembly's IL and signatures. Client and dedicated assemblies can have different method fingerprints even when their decompiled text looks identical.

In particular, Steam status reads must use the interface that owns the running build's connections. `SteamMetrics.Initialize()` inspects `GetSendQueueSize` and verifies agreement with `SendQueuedPackages`. Do not select the native API from OS, role alone, or `GetConnectionQuality`: the dedicated build's quality method can refer to the client API. Validate both client and server assemblies for changes in this area.

Decompiled source also omits prefab/asset configuration and is not proof of runtime or multiplayer behavior. Older local notes can still mention `urfmode/`; implementation lives under `mod/`. Keep game source, binaries and third-party investigation downloads out of Git and release ZIPs.

## Build and verification

Use version pins in `toolchain.lock.json`, `.config/dotnet-tools.json`, `package.json`, `bun.lock` and NuGet lockfiles. The mod targets **net48** for the game's Unity/Mono/BepInEx runtime; the newer .NET SDK builds it and runs the pure managed checks. Do not change the mod target framework merely to match the installed SDK. Bun and Svelte are the chosen dashboard stack.

For a new client-development checkout with Valheim installed, `rtk proxy python3 scripts/dev.py setup` downloads pinned local tools/libraries, creates the isolated profile, copies game references and restores dependencies. `scripts/prepare-ci.py` prepares server build references instead; it writes the same `.local/references/Managed` destination, so use it in an isolated CI/work checkout when preserving a client snapshot matters. `scripts/mono` uses system Mono or the optional `.tools/mono` runtime; setup does not guarantee Mono or Docker is installed.

Run the checks appropriate to the change from the repository root:

```sh
# Mod: verify references, build, then exercise managed policy/protocol checks.
rtk proxy python3 scripts/dev.py doctor
rtk proxy scripts/dotnet build mod/ValheimBoosted.csproj -c Release
rtk proxy scripts/dotnet run --project tests/TelemetryChecks/TelemetryChecks.csproj

# Real Harmony under Mono, followed by contracts against actual game assemblies.
rtk proxy scripts/dotnet build tests/IntegrationChecks/IntegrationChecks.csproj
rtk proxy scripts/mono tests/IntegrationChecks/bin/Debug/net48/IntegrationChecks.exe
rtk proxy scripts/dotnet build tests/GameContractChecks/GameContractChecks.csproj
rtk proxy scripts/mono tests/GameContractChecks/bin/Debug/net48/GameContractChecks.exe .local/references/Managed

# Network additions: actual game hooks and bounded connection replay. Also run with dedicated references when present.
rtk proxy scripts/dotnet build tests/NetworkIntegrationChecks/NetworkIntegrationChecks.csproj
rtk proxy scripts/mono tests/NetworkIntegrationChecks/bin/Debug/net48/NetworkIntegrationChecks.exe .local/references/Managed

# Dashboard: install only when dependencies need provisioning or updating.
rtk proxy bun install --frozen-lockfile
rtk proxy bun run check
rtk proxy bun test dashboard
rtk proxy bun run build

# Packaging: the full packaging command requires a Release build.
rtk proxy python3 -m unittest discover -s tests/packaging
rtk proxy python3 scripts/package-mod.py --check
rtk proxy python3 scripts/package-mod.py
```

When dedicated references exist, also run the game-contract executable with `.local/ci-server/valheim_server_Data/Managed`. To compile against them without replacing the client snapshot, pass an absolute `-p:GameReferences=...` and separate `-p:OutputPath=...` to the mod build. `scripts/dev.py build` makes a Debug build with client-reference freshness checks; `deploy` builds and copies into the local profile and handles the UrfMode migration. It does not deploy to the production container. `scripts/open-vscode` configures the local SDK environment.

For UI changes, inspect populated and empty/stale states in a browser, including mobile widths and actual rendered charts. Use temporary telemetry/report directories for fixtures. For networking changes, builds and mocked RPC/Harmony tests complement actual client/server contract checks and live multiplayer tests; report those levels separately. If Docker or a live game is unavailable, state what was not verified.

## Behavior to preserve when making changes

- Keep version/protocol gates, exact patch assertions, independent feature switches, overlap detection and vanilla fallback. Refreshing a hash after a game update requires reviewing the new target behavior, not just making the check pass.
- The fair scheduler is enabled by default for dedicated Steam servers, with vanilla behavior retained where inapplicable. Stage 2 alone preserves packet formats and existing queue limits. Stages 3–5 (`SendWindows`, `SteamRate`, `CaptainOwnership`, `Compression`) are independently default-on experiments in new configs. Preserve saved opt-outs on upgrades. Keep connection-only max-rate writes and reversible leases, bounded captain eligibility/transfer rules, and explicit per-direction compression negotiation with queued-frame draining. General NPC ownership reassignment remains future work.
- FreshInterest, ActorPriority, EarlyZdoBuffer, Map.FastUpdates and Map.ForceLocationSharing default to true. Keep relevance changes scoped to selection, vanilla priority tiers and reserved passes, bounded lossless FIFO replay, capability-only map payloads and server-side visibility enforcement without rewriting client preferences. General simulation ownership remains unchanged.
- Client sharing/reception default to true for installed compatible peers, with local switches and optional character names. Unmodded peers remain compatible. Local client JSON export is a separate setting and defaults off. HUD toggle is F8; the default lag marker is F9.
- Keep collection, send queues, RPC payloads and receive budgets bounded. Keep Unity access on the game thread and database/HTTP work in Bun. Preserve units, null/unavailable values, collection time versus receipt time, sample coverage and clock uncertainty. Correlation is not proof that a player caused lag.
- History defaults to seven days through `HISTORY_RETENTION_DAYS`; SQLite and saved reports share the persistent reports volume. `HISTORY_SAMPLES` controls recent in-memory overview charts, not persistent retention. Reset starts a new recording; it must not erase persistent history or saved reports.
- Every ready connection has a new stream/session ID. Steam account identity comes from the authenticated socket and a persistent, private `ClientTelemetry.IdentityKey` in the server config; preserve it across restarts. Group history by this server-scoped pseudonym, never by character name, IP or game peer ID. Other transports and older rows without identity remain session-specific. Treat connection endings inferred after missing snapshots or a server change as estimates. The SQLite v2 migration preserves raw history and rebuilds derived minute rollups; v3 appends metric layout 2 and v4 appends layout 3 without deleting history; layout-1/2 archives remain readable; never backfill identities by guessing.
- Player aggregates preserve metric weights across reconnects and archived rows: frame-weighted timing, duration-weighted FPS, observed-value means, summed counts, maxima for window percentiles. Offline time contributes no samples. LayerChart gaps must stay gaps; chart motion is disabled and missing values remain null.
- Changes to telemetry fields cross C# models/export, TypeScript validation, history/report aggregation and UI. Maintain compatibility with older snapshots and saved reports where intended. Update database/protocol versions deliberately when their format changes.
- Keep release versions consistent across the C# project/plugin, `package.json`, Thunderstore manifest and root `CHANGELOG.md`. Thunderstore uses numeric `Major.Minor.Patch`; pre-alpha is descriptive text. Produce both the Thunderstore ZIP and the convenient `-plugins.zip`, without another ZIP wrapper or bundled game/runtime dependencies. See `scripts/package-mod.py` for the exact layouts.
