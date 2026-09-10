# valheim-boosted observability — first pass

valheim-boosted 0.1.0 collects read-only diagnostics. It does not alter ownership, send budgets, send frequency, simulation speed, or the network protocol. There are no telemetry RPCs. The separate Svelte/Bun dashboard reads these snapshots; see [dashboard/README.md](../dashboard/README.md).

## Client HUD

Join a world with the development profile and press **F8** to toggle the panel in the upper-right corner by default. Change `HUD / Position` to choose another corner. It shows local frame intervals, local ZDO manager update duration, loaded object count, server RTT, RTT sample-to-sample variation, estimated transport queue delay, outstanding/unacknowledged bytes, and outgoing/incoming traffic. Values refresh once per second by default.

`n/a` means unmeasured or unavailable; it never means healthy or zero. Remote server/owner CPU is explicitly unmeasured. The first RTT sample has no variation value. The HUD does not diagnose another player's connection from visual symptoms.

Compatibility gates, exact hook contracts, and independent probe switches are documented in [Compatibility and diagnostic switches](COMPATIBILITY.md).

## Configuration

BepInEx generates `BepInEx/config/valheim.boosted.cfg` on the first load.

| Section / key | Default | Purpose |
| --- | --- | --- |
| Telemetry / Enabled | true | Enable collection; restart required. |
| Telemetry / SampleIntervalSeconds | 1 | Allowed range 0.5–10 seconds. |
| Telemetry / ExportOnServer | true | Export on dedicated servers and player hosts. |
| Telemetry / ExportOnClient | false | Optional local client JSON export. |
| Telemetry / ExportPath | `<BepInExRootPath>/valheim-boosted-telemetry/snapshot.json` | Use a unique path per process; restart to change. |
| HUD / Enabled | true | HUD visibility; F8 updates this setting. |
| HUD / ToggleKey | F8 | Configurable BepInEx keyboard shortcut. |
| HUD / Position | TopRight | Choose `TopRight`, `TopLeft`, `BottomRight`, or `BottomLeft`; 12-pixel inset from the selected edges. |

Edit the configuration before starting the game, or use a configuration manager that updates BepInEx entries at runtime. This plugin does not watch the configuration file for external edits.

Dedicated servers do not construct or render the HUD. Runtime role uses `ZNet.IsServer()` / `IsDedicated()`, including host mode for local player hosts. Client and dedicated-server reference contracts are checked separately; live dedicated-server behavior still requires the checks below.

## Snapshot interface

The version-1 JSON file is the interface for the separate dashboard process. Game objects are read only on the Unity thread. A background worker serializes a detached DTO, keeping at most one pending snapshot. It replaces the file atomically using a temporary file in the same directory. No network listener is created.

Example development-profile path:

```text
<repository>/.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json
```

For the container dashboard, share the directory, not a single bind-mounted file: atomic replacement changes the file's inode. Mount it read-only in the reader. A reader should reopen the file on each poll, check `schemaVersion`, and track `processSession`, `worldSession`, `sequence`, `capturedAtUtc`, `running`, and `role`. A practical initial staleness rule is no new sequence for three configured sample intervals. Old files can survive crashes; their presence is not a health check. Normal shutdown attempts to publish `running: false`; hard termination and blocked storage can prevent it. A final `menu` snapshot is published when an exporting world is left.

Exporter exceptions are visible in the HUD/log, with log warnings capped to one per 30 seconds. `exportError` reports the previously observed worker error, so it can lag recovery by a snapshot. When storage fails, the old file may remain unchanged; staleness detection is essential.

## Metric semantics

| Field | Meaning / scope |
| --- | --- |
| frameIntervalMs | Wall-clock intervals between this plugin's Unity Update callbacks. Includes waiting/frame limiting; not CPU execution time. |
| networkUpdateDurationMs | Wall-clock duration inside local `ZDOMan.Update(float)`, including other patches around that call. Not all networking or all physics. |
| fixedUpdates, fixedStepSeconds | FixedUpdate callback count in the sample window and Unity's configured step. Neither measures full physics execution cost. |
| managedMemoryBytes | `GC.GetTotalMemory(false)`; excludes Unity/native memory and is not process RSS. |
| gcCollections | Delta in generation 0/1/2 collection counters over the window; not pause duration. |
| knownZdos | Objects in the local ZDO manager, including state without loaded GameObjects. |
| zdosSentLastGameSecond / zdosReceivedLastGameSecond | Existing game counters for its last completed reporting interval; not packet counts or bytes. |
| clientChangedZdos | Client-side changed-object backlog, in objects; null on the server. |
| loadedObjects | ZNetScene's currently instantiated network-view count. |
| loadedOwnership | Counts by ZDO owner session ID among loaded objects. Owner `0` means unowned. At most the first 10,000 dictionary entries are counted; `ownershipStatus` marks truncation. This is not a whole-world ownership distribution. |
| peers[].rttMs | Steam transport RTT for that connection, from this process's perspective. |
| peers[].rttSampleDeltaMs | Absolute difference between successive sampled RTTs; not packet jitter. |
| peers[].localDeliveryQuality / remoteDeliveryQuality | Steam's 0–1 delivery-quality values; do not relabel as pure packet-loss percentages. |
| peers[].outgoingBytesPerSecond / incomingBytesPerSecond | Steam's reported recent traffic rates, not measured available bandwidth. |
| peers[].estimatedSendRateBytesPerSecond | Steam's send-rate estimate, which can be affected by configured limits. |
| peers[].applicationQueuedBytes | Bytes in Valheim's local Steam send queue, not yet submitted to Steam. |
| peers[].pendingReliableBytes / pendingUnreliableBytes | Steam-pending data; reliable pending data can include retransmissions. |
| peers[].sentUnacknowledgedReliableBytes | Already sent reliable data awaiting acknowledgment. |
| peers[].outstandingBytes | Application queue + Steam pending bytes + sent unacknowledged reliable bytes. Not exclusively unsent backlog. |
| peers[].estimatedTransportQueueMs | Steam's estimated waiting time before a new message starts sending, using its single-lane status. Excludes the game's application queue and end-to-end delay. Revisit if another mod enables multiple lanes. |
| peers[].lastZdoBatchReceivedAgoSeconds | Local elapsed time since the observed `RPC_ZDOData` handler completed for this peer, once peer tracking began. Idle connections can legitimately have large ages. Not freshness of any specific NPC. |
| peers[].zdoBatchesReceivedInWindow | Observed ZDO handler completions since the preceding sample. Does not imply each contained revision was accepted. |

Timing summaries contain count, mean, p95, and max. Percentiles use at most the latest 2048 measurements per window; `percentileSamples` makes truncation explicit. Mean/max/count cover the full window. Empty timing windows use null values. The first frame interval includes time since collector initialization. Steam native status failure leaves metrics null. Other transports are marked unsupported instead of reporting fabricated zeros. This release targets Steam transport.

Peer IDs are opaque game-session identifiers, not Steam IDs. Player names, IP addresses, passwords, world names, and packet contents are not exported. No counter-reset APIs are called. Optional hooks/field reads report unavailable status if installation fails. Other mods changing these internals still require compatibility testing.

## Verification and live checklist

The plugin builds against the installed Valheim client references. Pure managed checks run with the existing local SDK, without new packages:

```sh
./scripts/dotnet run --project tests/TelemetryChecks/TelemetryChecks.csproj
python3 scripts/dev.py build
python3 scripts/dev.py deploy
```

The executable checks cover bounded timing statistics, null JSON values, concurrent atomic reads, latest-snapshot delivery, shutdown flush, and exporter failure/recovery. These run under .NET 10; they do not prove Unity Mono, Harmony runtime patches, native Steam calls, or Mono filesystem replacement behavior.

Live checks still required:

1. Launch a development world using the existing BepInEx profile. Confirm the 0.1.0 log entry and that F8 toggles the HUD without affecting input.
2. Join a Steam server. Check RTT/traffic against observable activity. Local hosting does not have a server connection and shows host metrics instead.
3. Enable `ExportOnClient` for client export, or host a development world for default server export. Confirm increasing sequences and readable JSON.
4. Leave the world and quit; inspect menu/stopped status. Kill a development process separately to verify the dashboard detects stale output.
5. Validate the plugin and Steam calls on the matching dedicated-server build/container, with a writable export directory. Confirm no UI activity there.

Remote client CPU telemetry, ownership-transfer history, object-specific freshness, whole-server CPU/RSS, automatic mod-launched dashboard startup, and adaptive tuning are later work. None is implied by the current metrics.
