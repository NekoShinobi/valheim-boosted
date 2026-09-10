# Empty-server idle mode

Idle mode is enabled by default on supported dedicated servers. After 60 continuous seconds with no peers and no save in progress, it caps the main loop at 10 FPS and exports telemetry every five seconds. Clients and player-hosted worlds keep their normal behavior.

Edit `valheim.boosted.cfg` and restart to change these settings:

```ini
[IdleServer]
Enabled = true
DelaySeconds = 60
TargetFrameRate = 10
SampleIntervalSeconds = 5
```

The Docker configuration is `/config/bepinex/valheim.boosted.cfg`; a conventional BepInEx installation uses `BepInEx/config/valheim.boosted.cfg`. The master `Telemetry.Enabled` switch also disables idle mode.

| Setting | Allowed values | Behavior |
| --- | --- | --- |
| `Enabled` | true / false | Enable automatic idle control. |
| `DelaySeconds` | 10–600 | Continuous empty time before entering idle mode. A connection or save restarts the delay. |
| `TargetFrameRate` | 5–30 | Idle FPS cap. An existing lower cap stays lower. |
| `SampleIntervalSeconds` | 1–10 | Idle sampling interval; an existing slower normal interval is preserved. |

## Wake-up and restoration

Every main-loop update checks the complete peer list, including connections that have not completed their handshake. A new peer, a save, or leaving the dedicated world exits idle mode and restores the previous frame cap. At the default idle cap, detecting a join normally takes up to approximately one 100 ms frame, plus any engine stall. It does not wait for a telemetry sample. A transition triggers a fresh snapshot and restores the normal sampling cadence.

The controller also restores its frame cap on component disable and plugin shutdown. If another component changes the cap while idle, the controller preserves that change and suspends idle control until the next world. Runtime inspection failures restore the cap and disable the feature for that plugin instance. There are no patches to connection handling, saving, or world simulation.

## Measurements and limits

The dashboard's existing Diagnostics feature list includes `IdleServer`: `active` means idling, `available` means normal cadence or waiting for eligibility, and the detail explains the configured cap and interval. `setting_changed` reports a frame-cap conflict; `runtime_failed` reports a failed inspection. A target cap is not a measurement of achieved FPS.

Snapshots retain the actual elapsed `sampleWindowSeconds` and add optional `sampleIntervalSeconds` for the next scheduled interval. Update the metrics service with the plugin to use that interval when checking freshness, including the first idle snapshot. Older snapshots remain supported. Idle state is also carried by the existing feature reports without a schema-version change.

Main-loop frame intervals near 100 ms are expected at 10 FPS. Long-frame counters still count those intervals, so compare them with the idle state and player activity before interpreting them as lag. Physics retains its existing fixed timestep; reducing main-loop FPS does not guarantee a proportional CPU reduction. Memory remains allocated and Steam/background workers continue. No power or CPU savings are claimed until measured on a live server.

The empty-peer fair-scheduler path clears its state before creating peer lookup sets. Network processing, Steam callbacks, saves, physics timing and time scale retain their normal code paths.

## Live verification

1. Record at least five minutes with no peers and `IdleServer.Enabled = false` as a baseline.
2. Enable idle mode and restart. Wait past the delay, verify `IdleServer` is active and FPS approaches the configured cap, then record the same duration and compare process CPU.
3. Join while idle. Verify the original cap and telemetry cadence return during connection setup; check Steam and crossplay joining if both are used.
4. Disconnect and verify that the full delay runs again. Check saving, restart, and reconnects. Check a client and player host remain unaffected.

Automated tests cover policy transitions, the production controller with game fixtures, and telemetry freshness. These checks complement the live measurements above; they do not establish actual CPU savings or multiplayer behavior.
