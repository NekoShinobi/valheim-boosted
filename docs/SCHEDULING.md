# Stage 2: fair replication scheduling

Stage 2 enables an original fair scheduler by default for **dedicated Steam servers**, alongside replication and process diagnostics. Set `Scheduling.Enabled = false` and restart to capture a vanilla baseline. This is a pre-alpha implementation; automated checks do not establish multiplayer performance or gameplay acceptance.

## Configuration

New configurations generate the following defaults. Existing configurations retain their saved values. To change them, stop the server, edit `valheim.boosted.cfg` (`/config/bepinex/valheim.boosted.cfg` in the community Docker image), and restart:

```ini
[Scheduling]
Enabled = true
MaxCallsPerFrame = 4
TimeBudgetMs = 2
MaxDebtPerPeer = 2
```

The master `[Telemetry] Enabled` must also be true. All scheduling settings take effect on restart. Set `Scheduling.Enabled = false` and restart to restore vanilla scheduling. Other players do not need this mod.

## Behavior and fallback

The problem is the vanilla normal-send loop servicing one peer per frame after starting a round. At higher peer counts that extends time between opportunities for each connection. Our replacement gives each ready, connected peer service credit at a nominal 20 Hz, keeps a persistent fair cursor, and caps accumulated credit. Each peer can receive at most one opportunity in a frame. Connection removal, reconnects and world changes discard stale state.

A frame stops starting sends at either its call limit or its time limit. A single vanilla send is indivisible: it may exceed the soft time budget. That overrun is measured; the next peer retains its place for the next frame. After a hitch, excess credit is discarded instead of causing unbounded catch-up work. A 20 Hz target is not a guarantee when the frame rate or budgets cannot support it.

The scheduler calls the existing `SendZDOs(peer, false)` operation. Stage 2 alone preserves serialization, relevance discovery, forced sends, destroy processing, queue allowances and native transport settings. The independently switched [Stages 3–5](SERVER-IMPROVEMENTS.md) add allowances, connection maximum rates, captain assignment and negotiated compression. Explicit flush paths still run vanilla code. Player hosts, clients, and servers with ready non-Steam peers retain vanilla scheduling.

The adapter requires supported game/protocol versions, exact scheduler/send signatures, a reviewed fingerprint pair, expected private fields, and exclusive ownership of its patch points. It verifies its own patch registrations and foreign owners before every replacement call, plus a periodic audit. Failed initialization or changed/conflicting patches remove our hooks and restore vanilla. After an exception during a send, vanilla is not rerun in that same frame, because some state may already have been changed; later frames fall back normally. Failures appear in feature status and server logs.

## Measurements

`Replication` is an independent diagnostic switch under `[Features]`, enabled by default. It observes the same vanilla send operation with the scheduler off or on, making baseline comparisons possible. `ZdoReceive` must also be enabled for received payload bytes. `ConnectionHealth` reads heartbeat and managed queue information without requiring the native Steam status query. `ProcessResources` reports the Valheim process, not the separate dashboard container.

| Measurement | Meaning |
| --- | --- |
| Send attempts, batches, failures | Per-snapshot calls, true returns and exceptions from vanilla send processing; not delivery acknowledgments. |
| Empty or deferred | False returns combine no relevant changes with queue-budget deferrals; they are not all congestion. |
| ZDOs sent | Objects serialized in observed calls, not unique objects or wire bytes. |
| Received payload bytes | ZDOData package bytes observed on the existing receive hook; excludes transport/RPC framing. |
| Service age / interval p95 | Time since last send opportunity and intervals between opportunities, including explicit flush calls. |
| Send duration | Time spent inside the observed vanilla operation; includes other work it calls. |
| Scheduler work p95/max | Time in policy and send processing; setup and patch audits also incur small additional work. |
| Pending / discarded credit | Scheduler work units, not bytes or packets. |
| Managed queue packets/bytes | Local Steam application queue, independent of native pending/unacknowledged counters. Inspection stops above 4096 packets and reports unavailable byte totals. |
| Queue growth | Signed change in sampled queued bytes per second. |
| Queue nonempty duration | Time across nonempty samples; not the residence time of a particular packet. |
| Heartbeat age | Game elapsed time since a ping reply; not RTT and not an added probe packet. |
| CPU / RSS / threads | Process CPU (100% = one core), resident memory including native allocations, and thread count. CPU is unavailable until a second sample. This is not container quota utilization. |
| Frame p99 / long frames | Bounded-window p99, counts of frame intervals ≥50 and ≥100 ms; includes frame limiting and waits. |
| Save timings | Game-reported elapsed and preparation times. Reported elapsed includes completion-notification delay and does not establish disk-write duration or save success. |
| Mod build ID | Running mod assembly MVID, useful when testing several builds with version 0.1.0. |

## Validation

Automated checks exercise fairness under a saturated ten-peer workload, capped hitch debt, slow sends, disconnects/reconnects, duplicate eligibility, empty worlds, client/non-Steam fallback, patch conflicts, cleanup, and no replay of a failed partial round. Game-contract checks validate both client and dedicated-server reference assemblies. The dashboard validates optional new fields and still accepts older snapshots.

For acceptance testing, use an isolated world and compare identical 2/4/8/10-player workloads with the scheduler off and on. Track service interval/age alongside send duration, frame p95/p99/max and limited-frame counts. Test a large join, teleport, disconnect/reconnect, a slow connection, explicit save/flush and shutdown. Verify world state and interactions, not only frame rate. A successful one-player idle session is not sufficient evidence of improvement.

Container cgroup throttling, per-message oldest queue age and general NPC ownership reassignment remain future work. Targeted ship ownership and native rate/window experiments are described in [Stages 3–5](SERVER-IMPROVEMENTS.md). Optional [client performance reports](HISTORY.md) provide frame/CPU/GC and connection context through a separate bounded telemetry protocol; scheduler decisions do not depend on those self-reported measurements.
