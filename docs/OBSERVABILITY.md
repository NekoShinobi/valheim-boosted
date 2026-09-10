# valheim-boosted observability — first pass

valheim-boosted 0.1.0 collects diagnostics and enables the [Stage 2 scheduler](SCHEDULING.md) by default for dedicated Steam servers. Stage 2 alone changes normal send scheduling while preserving ownership, vanilla packet formats, queue allowances and native rate settings. [Stages 3–5](SERVER-IMPROVEMENTS.md) are separately opt-in experiments. Optional peer telemetry RPCs carry bounded client performance summaries to compatible servers. The separate Svelte/Bun dashboard reads these snapshots and retains history; see [dashboard/README.md](../dashboard/README.md).

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
| Telemetry / ExportPath | `/config/valheim-boosted/telemetry/snapshot.json` | Use a writable, unique path per process; restart to change. |
| HUD / Enabled | true | HUD visibility; F8 updates this setting. |
| HUD / ToggleKey | F8 | Configurable BepInEx keyboard shortcut. |
| HUD / Position | TopRight | Choose `TopRight`, `TopLeft`, `BottomRight`, or `BottomLeft`; 12-pixel inset from the selected edges. |

Edit the configuration before starting the game, or use a configuration manager that updates BepInEx entries at runtime. This plugin does not watch the configuration file for external edits.

Dedicated servers do not construct or render the HUD. Runtime role uses `ZNet.IsServer()` / `IsDedicated()`, including host mode for local player hosts. Client and dedicated-server reference contracts are checked separately; live dedicated-server behavior still requires the checks below.

## Snapshot interface

The version-1 JSON file is the interface for the separate dashboard process. Game objects are read only on the Unity thread. A background worker serializes a detached DTO, keeping at most one pending snapshot. It replaces the file atomically using a temporary file in the same directory. No network listener is created.

The export default matches the README's Valheim container setup. Existing configurations retain their saved `ExportPath`; edit it and restart to adopt the new default. For local development or a non-container installation, explicitly set `ExportPath` to a writable absolute path, for example:

```text
<repository>/.local/profile/BepInEx/valheim-boosted-telemetry/snapshot.json
```

For the container dashboard, share the directory, not a single bind-mounted file: atomic replacement changes the file's inode. Mount it read-only in the reader. A reader should reopen the file on each poll, check `schemaVersion`, and track `processSession`, `worldSession`, `sequence`, `capturedAtUtc`, `running`, and `role`. A practical initial staleness rule is no new sequence for three configured sample intervals. Old files can survive crashes; their presence is not a health check. Normal shutdown attempts to publish `running: false`; hard termination and blocked storage can prevent it. A final `menu` snapshot is published when an exporting world is left.

Exporter exceptions are visible in the HUD/log, with log warnings capped to one per 30 seconds. `exportError` reports the previously observed worker error, so it can lag recovery by a snapshot. When storage fails, the old file may remain unchanged; staleness detection is essential.

## Stage 2 additions

Snapshots now include optional `scheduler` and `resources` objects, `modBuildId`, frame p99 and long-frame counts, save state/timings, and per-peer `replication`, heartbeat and managed queue measurements. See the [metric definitions and limitations](SCHEDULING.md#measurements). The dashboard shows these in **Resources & scheduling** and **Replication & heartbeat**. Optional fields preserve compatibility with older snapshots.

`[Features] Replication`, `ConnectionHealth`, and `ProcessResources` default to true and can be disabled independently on restart. `ZdoReceive` must remain enabled to measure received ZDO payload bytes. Managed queue bytes remain available if Steam's native status query throws; the byte scan is capped at 4096 queued packets.

## Connection measurement failures

The dashboard's **live** indicator means snapshots are arriving; it does not mean every probe succeeds. A banner identifies connected peers with unavailable measurements, and **Diagnostics → Connection diagnostics** shows the failing operation, underlying exception type/message, and exception wrapper chain. `TargetInvocationException` is unwrapped so the actual native/library error can be identified. Older snapshots remain supported and show a prompt to update the mod when error details are missing.

Each peer exports optional `measurementError` fields (`exceptionType`, `message`, `operation`, `exceptionChain`), cleared in a fresh successful sample. Messages are bounded to 512 characters and operations to 256; full stack traces stay in BepInEx's server log. Error messages can include runtime library names or local paths. Look for `Steam telemetry failed` in that log. Warnings share a 30-second limit across peers, including changing exceptions and unsuccessful native result codes.

`SteamTransport` becomes `degraded` if any attempted peer query fails, continues retrying each sample, and returns to `active` when all queries succeed. With no connected Steam peers it reports `available` and explains that it is waiting. Its cumulative calls/attempts count includes failed queries. Other working probes and snapshot export continue independently.

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

Peer IDs are opaque game-session identifiers, not Steam IDs. Core peer metrics omit IP addresses, passwords, world names and packet contents. Optional client telemetry adds server-known character labels (`IncludePlayerNames`, default true), fresh connection stream IDs and server-scoped Steam account pseudonyms for returning-player history. The private identity key and raw Steam IDs are never exported. See [player identity and sessions](HISTORY.md#returning-players-and-login-sessions). No counter-reset APIs are called. Optional hooks/field reads report unavailable status if installation fails. Other mods changing these internals still require compatibility testing.

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

Remote client frame/CPU telemetry and seven-day history are described in [HISTORY.md](HISTORY.md). Ownership-transfer history, object-specific freshness, whole-machine CPU/RSS, automatic mod-launched dashboard startup, and adaptive tuning remain later work.

## Stages 3–5 measurements

`serverImprovements` exports configured send-window/rate ceilings, compression budget, Steam config interface, captain eligibility/status and per-window transfers/deferrals, compression raw/framed bytes, sent/received/skipped/rejected counts and encode/decode timing summaries. Savings compare the original bytes with the full compression envelope, before Steam framing; they cover only compressed sends. CPU timing has null percentiles when no operation ran.

`peers[].improvements` exports the applied ZDO allowance/reason, original and effective maximum rate, unchanged minimum rate, cumulative connection write/readback/restore failures, and compression negotiation/drain status. A configuration flag alone does not prove application. Closed connections have no further samples. These fields are optional for compatibility with older snapshots.

The overview, report comparisons and persistent LayerChart history expose these fields. See [Server improvements](SERVER-IMPROVEMENTS.md) for exact units, bounds, fallback and live acceptance scenarios.
