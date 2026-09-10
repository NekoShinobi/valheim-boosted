# Compatibility and diagnostic switches

Stage 1 provides read-only telemetry with explicit compatibility and patch checks. Supported references are **Valheim 1.0.7, network protocol 39**, covering the inspected Linux client and dedicated-server builds. Support here means inspected contracts and automated checks; it is not a claim of completed multiplayer acceptance testing.

## Runtime checks

The mod reads the running assembly's game version and protocol through reflection, so the protocol cannot accidentally be a constant copied into the plugin at compile time. Unknown versions disable game-dependent probes. Local frame measurements and diagnostic status remain available. The Jötunn declaration explicitly uses `NotEnforced` / `None`: no mandatory client installation or matching mod version is introduced. There is no custom telemetry handshake.

Before applying either Harmony hook, it checks the declared instance method, exact parameter and return types, and SHA-256 of its IL. The reviewed client and server receive-method variants are explicitly allowlisted; their decompiled C# is identical, but their IL fingerprints differ. A changed target disables that probe with the observed fingerprint in its diagnostic detail. CI checks downloaded references against the same allowlist, so an upstream update cannot silently redefine the baseline.

| Target | Reviewed IL SHA-256 |
| --- | --- |
| `ZDOMan.Update(float)` — client/server | `6abb99226c81efa7feae749ccdb6744751e4dbef9df639ee4fbe2a623c8488a2` |
| `ZDOMan.RPC_ZDOData(ZRpc, ZPackage)` — client | `8cce5e4fde99f56bd1c6a719a8a5788f5b465a887f89f72eb9693a9a3aaf20a3` |
| `ZDOMan.RPC_ZDOData(ZRpc, ZPackage)` — server | `79287d6cb3029b2c5f15d45b857ca105e5296ae1bbf3049d0ff0102c2a0e7792` |

Any existing foreign Harmony owner on a target blocks our hook. Registration is checked after installation; a five-second audit detects removed hooks or new overlapping owners and removes only our hooks. This is conservative overlap detection, not a complete compatibility catalogue for other mods. Harmony cannot detect every possible native detour or behavioral interaction.

Steam collection validates the connection/queue field types and inspects the running game's own `GetConnectionQuality` IL to identify its status-query interface. It accepts exactly one matching client or game-server wrapper with the expected signature. Both inspected builds use `Steamworks.SteamNetworkingSockets`. The native query's return code still determines whether a measurement is available; no native Steam call is made during contract inspection.

## Feature configuration

Edit `BepInEx/config/valheim.boosted.cfg`, then restart Valheim. These switches intentionally apply at startup; there is no partial live patch reconfiguration.

```ini
[Telemetry]
Enabled = true

[Features]
FrameTiming = true
GameCounters = true
NetworkTiming = true
ZdoReceive = true
SteamTransport = true
Ownership = true
```

| Switch | Controls |
| --- | --- |
| `Telemetry.Enabled` | Master switch. False starts no collector, hooks, HUD, or exporter. The disabled state is logged. |
| `FrameTiming` | Local frame-interval sampling |
| `GameCounters` | Known/sent/received ZDO and client backlog counters |
| `NetworkTiming` | Harmony timing of `ZDOMan.Update` |
| `ZdoReceive` | Harmony observation of received ZDO batches |
| `SteamTransport` | Native Steam connection/queue measurements |
| `Ownership` | Loaded-object count and owner distribution scan |

HUD visibility and server/client export retain their separate settings. Turning export off does not disable collection. Disabling a probe leaves its measurements unavailable and preserves an explicit status; an absent measurement is not a healthy zero.

## Reading status

JSON snapshots include additive `compatibility` and `features` fields under schema version 1. Older snapshots remain supported by the dashboard. The HUD shows compatibility and both hook states; the dashboard's **Diagnostics** section shows all probes, reasons, the selected Steam interface, and callback counts. Compatibility/probe status is copied before background serialization.

- `available`: an enabled, unpatched probe has passed its startup checks; per-peer/ownership measurement status reports sampling failures separately.
- `installed_waiting`: the hook is registered, but no callback has been observed. An idle receive hook can legitimately remain here.
- `active`: at least one callback was observed. The cumulative invocation count measures hook execution, not successful packet delivery.
- `configured_disabled` / `blocked_compatibility`: disabled by configuration or unsupported game/protocol.
- `signature_mismatch` / `fingerprint_mismatch` / `contract_mismatch`: an expected method, body, field, or Steam interface differs.
- `conflicting_patch` / `patch_changed`: another owner overlaps the target, or our registration changed.
- `installation_failed` / `probe_failed`: hook installation or callback collection failed. Diagnostic callbacks preserve the game's original exception.

## Verification

Pure policy/export checks run under .NET 10. Hook lifecycle tests use the shipped Harmony DLL under Mono with minimal game/config fixtures. They check disabled behavior, changed-body rejection, original return/exception behavior, callback observation, conflict handling, and cleanup. Game-contract checks inspect the actual client/server assemblies under Mono without starting Unity or invoking Steam.

```sh
./scripts/dotnet run --project tests/TelemetryChecks/TelemetryChecks.csproj
./scripts/dotnet build tests/IntegrationChecks/IntegrationChecks.csproj
./scripts/mono tests/IntegrationChecks/bin/Debug/net48/IntegrationChecks.exe
./scripts/dotnet build tests/GameContractChecks/GameContractChecks.csproj
./scripts/mono tests/GameContractChecks/bin/Debug/net48/GameContractChecks.exe .local/references/Managed
```

CI installs Mono on its runner after fetching references and runs all three sets of checks. Local Mono is optional; it is only required to run the latter two test executables. The wrapper uses a system Mono if present, or the project-local runtime under `.tools/mono`.

Before enabling any Stage 2 networking changes, complete [the live checks](OBSERVABILITY.md#verification-and-live-checklist) on a dedicated server with multiple clients. Compare all probes off/on, confirm statuses and callback counts, and test reconnects, shutdown, unsupported versions, and overlapping mods. These checks still require an actual game session; compile success and fixture tests do not establish multiplayer behavior.
