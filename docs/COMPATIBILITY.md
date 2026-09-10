# Compatibility and diagnostic switches

Stage 1 provides telemetry with explicit compatibility and patch checks. [Stage 2](SCHEDULING.md) adds an independently switched fair replication scheduler and additional metrics. Supported references are **Valheim 1.0.7, network protocol 39**, covering the inspected Linux client and dedicated-server builds. Support here means inspected contracts and automated checks; it is not a claim of completed multiplayer acceptance testing.

## Runtime checks

The mod reads the running assembly's game version and protocol through reflection, so the protocol cannot accidentally be a constant copied into the plugin at compile time. Unknown versions disable game-dependent probes. Local frame measurements and diagnostic status remain available. The Jötunn declaration explicitly uses `NotEnforced` / `None`: no mandatory client installation or matching mod version is introduced. The optional client telemetry protocol negotiates capabilities independently; absent or incompatible telemetry never rejects a game connection.

Before applying a diagnostic Harmony hook, it checks the declared instance method, exact parameter and return types, and SHA-256 of its IL. The reviewed client and server receive-method variants are explicitly allowlisted; their decompiled C# is identical, but their IL fingerprints differ. A changed target disables that probe with the observed fingerprint in its diagnostic detail. CI checks downloaded references against the same allowlist, so an upstream update cannot silently redefine the baseline.

| Target | Reviewed IL SHA-256 |
| --- | --- |
| `ZDOMan.Update(float)` — client/server | `6abb99226c81efa7feae749ccdb6744751e4dbef9df639ee4fbe2a623c8488a2` |
| `ZDOMan.RPC_ZDOData(ZRpc, ZPackage)` — client | `8cce5e4fde99f56bd1c6a719a8a5788f5b465a887f89f72eb9693a9a3aaf20a3` |
| `ZDOMan.RPC_ZDOData(ZRpc, ZPackage)` — server | `79287d6cb3029b2c5f15d45b857ca105e5296ae1bbf3049d0ff0102c2a0e7792` |

Any existing foreign Harmony owner on a target blocks our hook. Registration is checked after installation; a five-second audit detects removed hooks or new overlapping owners and removes only our hooks. This is conservative overlap detection, not a complete compatibility catalogue for other mods. Harmony cannot detect every possible native detour or behavioral interaction.

Steam collection validates the connection/queue field types and inspects the running game's `GetSendQueueSize` IL to identify its status-query interface. It accepts exactly one matching client or game-server wrapper with the expected signature and verifies that the interface also matches `SendQueuedPackages`. The inspected client build uses `Steamworks.SteamNetworkingSockets`; the dedicated-server build uses `Steamworks.SteamGameServerNetworkingSockets`. The server's `GetConnectionQuality` still references the client API, so it cannot identify the correct transport context. The native query's return code still determines whether a measurement is available; no native Steam call is made during contract inspection.

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
Replication = true
ConnectionHealth = true
ProcessResources = true
```

| Switch | Controls |
| --- | --- |
| `Telemetry.Enabled` | Master switch. False starts no collector, hooks, HUD, or exporter. The disabled state is logged. |
| `FrameTiming` | Local frame-interval sampling |
| `GameCounters` | Known/sent/received ZDO and client backlog counters |
| `NetworkTiming` | Harmony timing of `ZDOMan.Update` |
| `ZdoReceive` | Harmony observation of received ZDO batches |
| `SteamTransport` | Native Steam connection/queue measurements |
| `Replication` | ZDO send attempts, outcomes, durations and per-peer service age |
| `ConnectionHealth` | Heartbeat and bounded managed Steam queue inspection |
| `ProcessResources` | Valheim process CPU, RSS and thread count |
| `Ownership` | Loaded-object count and owner distribution scan |

HUD visibility and server/client export retain their separate settings. Turning export off does not disable collection. Disabling a probe leaves its measurements unavailable and preserves an explicit status; an absent measurement is not a healthy zero.

## Reading status

JSON snapshots include additive `compatibility` and `features` fields under schema version 1. Older snapshots remain supported by the dashboard. The HUD shows compatibility and both hook states; the dashboard's **Diagnostics** section shows all probes, reasons, the selected Steam interface, and callback counts. Compatibility/probe status is copied before background serialization.

- `available`: an enabled, unpatched probe has passed its startup checks; per-peer/ownership measurement status reports sampling failures separately.
- `installed_waiting`: the hook is registered, but no callback has been observed. An idle receive hook can legitimately remain here.
- `active`: at least one hook callback was observed; for SteamTransport, all peer queries in the latest sample succeeded. The cumulative count measures hook calls or Steam query attempts (including failures), not successful packet delivery.
- `degraded`: one or more Steam peer queries failed in the latest sample. Queries continue; details appear in connection diagnostics and the rate-limited server log. Successful samples clear this state.
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

Stage 2 is enabled by default in new configurations; existing saved values are preserved. Set `Scheduling.Enabled = false` and restart for a vanilla baseline. Before deploying it to a production world, complete [the live checks](OBSERVABILITY.md#verification-and-live-checklist) on a dedicated server with multiple clients. Compare all probes off/on, confirm statuses and callback counts, and test reconnects, shutdown, unsupported versions, and overlapping mods. These checks still require an actual game session; compile success and fixture tests do not establish multiplayer behavior.

## Stage 2 contracts

`ReplicationContracts` verifies the following reviewed client/server pairs, exact return/parameter types, and the `m_peers`, `m_peer`, `m_zdosSent`, `m_sendTimer`, `m_nextSendPeer` field contracts. The two inspected method bodies decompile identically across the client and dedicated server; IL token differences require separate fingerprints.

| Method | Client SHA-256 | Dedicated SHA-256 |
| --- | --- | --- |
| `SendZDOToPeers2(float)` | `94e2e1fde2a1814725f7a221eacd34bc9d14ae83aa12f7f0f0199c72a39aa874` | `3f07a7733c48a5e6dededb1869bd4f5009ac66848aa4c5c9214142b71ffab401` |
| `SendZDOs(ZDOPeer, bool)` | `c5b10b489a5f5448da36708199e5955e3e3033de801c2853e4291a55a6ecbfb8` | `ee39ad2d80aaa2fbf761a61e7ad89d1dfb03653efc8c6f149138638b092a69be` |

Stage 2 hooks have a separate Harmony owner, `valheim.boosted.replication`. `FairScheduler` and `Replication` report their status in the existing feature collection. `runtime_failed` disables the affected integration and falls back to vanilla; `installed_waiting` can indicate client/non-Steam operation where vanilla is retained.

## Stages 3–5 contracts

The opt-in [server improvements](SERVER-IMPROVEMENTS.md) retain version/protocol gates and independent configuration. Send windows and compression share `valheim.boosted.replication` and the exact `SendZDOs` contract above. Their transform asserts exactly two allowance constants and one ZDO RPC dispatch; the actual client and dedicated methods are patched under Mono during contract tests. Forced flush allowances stay vanilla.

`SteamRateAdapter` verifies the native wrapper signatures and client/server initialization guards against the socket send interface before any connection-scoped write. `CaptainContracts` verifies the reviewed grant/release, player attachment, transform sync, ship-owner-update and owner-write fingerprint set, rejecting foreign Harmony owners. Compression additionally checks the existing receiver's signature/fingerprint and foreign owners, then negotiates format/limits separately for every connection and direction. Disabled or incompatible negotiation retains vanilla sends; malformed compressed data after negotiation closes only that connection.

`SendWindows`, `SteamRate`, `CaptainOwnership` and `Compression` appear separately in feature status. All default off. Native write/restoration failures, external overrides and receive-drain state are observable; native restoration can fail and must not be represented as successful rollback. Game-contract tests compile the real send method but do not run Unity simulation or native Steam. A headless missing `UnityEngine.Time` internal-call warning is expected from this JIT exercise.
