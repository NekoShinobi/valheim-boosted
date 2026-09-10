# Compatibility and diagnostic switches

The mod targets **Valheim 1.0.7, network protocol 39** on Steam. Game-version and patch checks can disable unsupported features while leaving other diagnostics available. Clients without the mod can still connect; client performance sharing and compression require compatible participating peers.

## Runtime checks

Unknown game versions disable game-dependent features. Changed game methods or overlapping patches from another mod also block the affected feature. The reason appears in **Live → Diagnostics** and the BepInEx log. Frame measurements and diagnostic status remain available when their own prerequisites are met.

Patch registrations are monitored while running. If a required hook changes or fails, the affected networking integration falls back to vanilla behavior. This does not guarantee compatibility with every other mod: native detours and indirect behavior changes may not be detectable. See [scheduling](SCHEDULING.md) and [server improvements](SERVER-IMPROVEMENTS.md) for feature-specific fallback behavior.

Steam measurements automatically select the interface used by the running game's connections. A failed native query leaves its measurements unavailable; other probes and export continue. See [connection troubleshooting](OBSERVABILITY.md#connection-measurement-failures).

## Feature configuration

Edit `BepInEx/config/valheim.boosted.cfg`, or `/config/bepinex/valheim.boosted.cfg` in the community server image, then restart Valheim.

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

Open **Live → Diagnostics** for feature states, reasons, the selected Steam interface and callback counts. The HUD also shows compatibility and hook status.

- `available`: an enabled, unpatched probe has passed its startup checks; per-peer/ownership measurement status reports sampling failures separately.
- `installed_waiting`: the hook is registered, but no callback has been observed. An idle receive hook can legitimately remain here.
- `active`: at least one hook callback was observed; for SteamTransport, all peer queries in the latest sample succeeded. The cumulative count measures hook calls or Steam query attempts (including failures), not successful packet delivery.
- `degraded`: one or more Steam peer queries failed in the latest sample. Queries continue; details appear in connection diagnostics and the rate-limited server log. Successful samples clear this state.
- `configured_disabled` / `blocked_compatibility`: disabled by configuration or unsupported game/protocol.
- `signature_mismatch` / `fingerprint_mismatch` / `contract_mismatch`: an expected method, body, field, or Steam interface differs.
- `conflicting_patch` / `patch_changed`: another owner overlaps the target, or our registration changed.
- `installation_failed` / `probe_failed`: hook installation or callback collection failed. Diagnostic callbacks preserve the game's original exception.

Scheduling, send windows, Steam rate tuning, captain ownership and compression have independent switches. All default to enabled in new configs; existing saved settings are preserved. An enabled setting does not guarantee the feature is active. Inspect its status and the per-connection measurements after restarting.
