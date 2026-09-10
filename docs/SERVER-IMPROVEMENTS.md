# Experimental server improvements

These independently switched features extend the Stage 2 scheduler. **All four `Enabled` settings default to `true` in new configs.** They require the reviewed Valheim 1.0.7 / protocol 39 contracts. These features are experimental; compare their effect on your own workload.

## Configuration and upgrades

Stop Valheim, edit its generated `valheim.boosted.cfg`, and restart. On the community server image the persistent file is `/config/bepinex/valheim.boosted.cfg`. Keep `[Telemetry] Enabled = true`. Existing saved settings are preserved: older configs keep `false` until their four entries are set to `true`. New configs generate the following values:

```ini
[SendWindows]
Enabled = true
TargetBytesPerSecond = 153600
MaximumBytes = 32768

[SteamRate]
Enabled = true
MaximumBytesPerSecond = 307200

[CaptainOwnership]
Enabled = true

[Compression]
Enabled = true
EncodeBudgetMs = 1
```

Set any `Enabled` entry to `false` to opt out of that feature. Send windows, Steam maximum-rate changes and captain assignment apply on the **dedicated Steam server**; clients may remain unmodded. Compression requires it to be enabled on **both endpoints** and successful per-connection negotiation. It supports Steam clients and player hosts as well as dedicated servers. No additional game ports, native libraries or dashboard control endpoint are added.

The four switches are independent of `Scheduling.Enabled`. For a vanilla replication baseline, disable scheduling, these four switches and the replication/map switches below, leaving diagnostics enabled. For a Stage 2 baseline, leave scheduling on. Use the dashboard's **Reset stats**, **Save report**, **Compare reports** and **History** to compare one experiment at a time. The dashboard displays measurements; config changes require a game restart.

## Stage 3: per-peer allowances and Steam maximum rate

`SendWindows` adjusts the per-peer budget for assembling ZDO updates while preserving normal object selection and state. The allowance is a budget for constructing a batch; a single serialized object can exceed it. It is not Steam's send-buffer capacity or a hard wire-packet size.

Once per second, each ready Steam peer gets a bounded allowance calculation using RTT, Steam's current send-rate estimate and **unsent** queue signals. After three healthy observations, the target is `min(configured ceiling, estimated rate) × RTT + 4096`, clamped between 10 KiB and the configured maximum (32 KiB by default; 64 KiB absolute limit). Growth is limited to 2 KiB per observation. RTT contributes at most one second to the calculation.

Two consecutive observations of queue pressure halve the allowance toward the vanilla floor. Pressure means estimated queue delay above 100 ms, a nonempty managed application queue, or pending reliable bytes above `max(10 KiB, estimated rate / 10)`. Already-sent unacknowledged bytes are not used as evidence of unsent congestion. Missing, invalid or over-three-second-old measurements restore the vanilla allowance. Explicit flushes retain the vanilla allowance; client, player-host and non-Steam send paths do too. No client-reported FPS enters this policy.

`SteamRate` separately raises a ready connection's `SendRateMax` to the requested maximum (300 KiB/s by default), preserving a higher existing limit and never writing `SendRateMin`. It does **not** promise that throughput will reach this ceiling. Steam retains its congestion control and the game's existing minimum rate. The adapter chooses `SteamNetworkingUtils` or `SteamGameServerNetworkingUtils` from the running socket's reviewed send interface, checks wrapper signatures and initialization guards, reads current maximum/minimum and inheritance, writes only the connection scope, and verifies readback.

An inherited setting is restored by removing the connection override; an explicit setting is restored to its prior value. This follows Valve's [connection configuration API](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/include/steam/isteamnetworkingutils.h). Readback failures trigger restoration; failed restoration is reported, with up to three attempts while the connection remains tracked. A different value or a cleared override indicates another writer, so the mod stops managing that connection's maximum. Identical writes by another mod cannot be distinguished. A native API failure can prevent restoration until the connection closes; diagnostics do not claim a successful rollback in that case. Global settings and buffer sizes are never written.

## Stage 4: ship captain ownership

The dedicated server examines at most 64 peer entries once per second, using replicated ZDOs already in memory. A candidate must have a ready, connected Steam peer; own the character ZDO associated with that peer; match the ship's **granted** helm-user player ID; be attached to that ship through `SyncTransform`; have a replicated relative position within 1.5 m of the prefab's helm attachment; and have the ship inside the peer's active area. Ambiguous candidates are skipped.

The allowlist is `Raft`, `Karve`, `VikingShip` and `Drakkar`; the registered prefab must actually expose a `Ship` and helm attachment. Unknown or modified layouts can fail eligibility. Prefab data is inspected at runtime without creating ship instances or loading distant zones. This does not establish that every asset variant has passed gameplay testing.

Eligibility must persist for one second. Transfers have a five-second cooldown, including brief dismounts, and at most two ships transfer per scan. At most 128 policy records are retained; unused records expire after ten seconds. A transfer calls the existing `ZDO.SetOwner` and forces replication of that ZDO. Unowned ships, disconnected/detached captains and already-correct owners are left to vanilla handling. Disabling the feature stops further assignments; it does not undo an otherwise valid owner assignment.

This aligns a ship with the person steering it. It does not choose the strongest computer, move NPC simulation to the dedicated server, or rebalance general world ownership. Other interactables and NPCs remain outside Stage 4's implemented scope.

## Stage 5: negotiated lossless ZDO compression

Only normal `ZDOData` payload dispatch is eligible. The vanilla serializer first produces its unchanged byte sequence; an independent Deflate frame may then be sent through `valheim.boosted.zdo.v1`. The receiver reconstructs the exact original bytes and calls the reviewed vanilla `RPC_ZDOData`. Routed gameplay RPCs, handshakes, zone discovery and destruction RPCs keep their existing format.

Each direction negotiates compression separately. Unmodded, disabled, unsupported or silent peers retain vanilla messages; reconnecting requires a new negotiation. Batches smaller than 512 bytes or larger than 64 KiB bypass compression. Compression must save more than 64 bytes and at least 5% after its envelope, so incompressible data uses vanilla sends.

Encoding runs synchronously on the game thread with a default **1 ms soft budget per frame across peers**. One bounded batch can exceed that budget; subsequent batches use vanilla. Decode allocation is limited to 64 KiB per message. These are bounded operations, not a hard total CPU guarantee. The existing game receive loop handles message scheduling. There are at most 64 negotiated connections. No separate queues or mutable compression dictionaries are introduced.

Dispatch occurs once, after the final bytes are chosen. Steam's existing send queue retries those bytes unchanged; the mod never re-encodes a queued frame or repeats a partially committed ZDO send. Small/incompressible/over-budget payloads and encoding failures use vanilla sends. A runtime fallback stops compressed sending and asks peers to stop, while retaining decoders for frames already queued. Hot-unloading the decoder disconnects negotiated receiving peers so they cannot keep sending to an absent handler; use normal restarts for deployment.

Malformed or unnegotiated compressed payloads close the offending connection. Silently dropping them would lose already-committed sender revision state, and interpreting their framing as vanilla would be incorrect. A failed capability offer alone does not disconnect an unmodded or incompatible peer. Compression checks the receive-method contract and blocks overlapping foreign receive hooks; Stage 2/3/5 share the reviewed send pipeline and its conflict fallback.

## Replication and map improvements

New entries default to `true`; saved opt-outs remain unchanged. Change them in `valheim.boosted.cfg` and restart the affected game process. No additional network ports or mod dependencies are needed.

```ini
[Replication]
FreshInterest = true
ActorPriority = true
EarlyZdoBuffer = true

[Map]
FastUpdates = true
ForceLocationSharing = true
```

- **FreshInterest:** a dedicated Steam server uses the peer's character position for ZDO relevance selection after observing a changed character revision. It must be at most two seconds old, owned by that peer, and within 32 metres of the vanilla reference position. Missing/stale characters, teleports and distant custom reference modes use vanilla selection. The shared reference position and ownership rules are unchanged.
- **ActorPriority:** players, ships and other characters receive bounded score bonuses inside the existing server priority tiers. Every fourth pass per peer uses vanilla ordering; forced sends retain their position. The game still performs one sort. This changes send order, not simulation ownership.
- **EarlyZdoBuffer:** clients register an early server-data handler before replication setup finishes. Queued and newly arriving packets replay in FIFO order after `AddPeer`, including decoded compression frames. Limits are 128 packets, 2 MiB total, 256 KiB per packet and 15 seconds for the oldest packet. Replay is limited to four complete packets and a soft 1 ms per frame. Overflow, expiry or replay failure closes that connection so reconnecting can rebuild consistent state. The normal handler is restored after draining; disconnect/world changes release the queue.
- **FastUpdates:** dedicated Steam servers send a complete set of public map positions twice per second to clients that request this capability. The packet contains character IDs and coordinates, without names. Each recipient gets at most 1,550 payload bytes per update (64 players); servers with more than 64 ready players retain vanilla updates. These optional sends defer when the game's send queue exceeds 10 KiB. Clients interpolate over half a second, snap large teleports, remove missing/private markers, and fall back to vanilla after two seconds without updates. No extrapolation or object movement is performed. Unmodded clients receive the normal player list.
- **ForceLocationSharing:** the dedicated server publishes every player's position through Valheim's normal player list, including players without the mod and players whose voluntary toggle is off. Set this server setting to `false` to respect voluntary visibility. Client preferences are never rewritten, and the HUD indicates the server's requirement after negotiation. No-map worlds keep their normal map restrictions. The force policy works independently of fast map updates.

These are original implementations of the missing capabilities identified in the local Smoothbrain Network comparison. Keep that mod disabled alongside overlapping patches: each feature checks reviewed game contracts and foreign Harmony owners before use. This does not add general NPC ownership reassignment or replace the existing conservative Steam rate policy with global buffer/rate overrides.

## Reading the dashboard

The snapshot adds optional `serverImprovements` and `peers[].improvements`. **Live → Server → Server improvements** shows configured switches, actual feature statuses, per-peer allowances, requested/original/effective Steam limits, write failures, captain transfers, compression agreement, savings and encode/decode timings. Timing windows without work remain null. Compression counters reset at snapshot capture; rate failures are cumulative for the connection.

Saved report metadata includes all new settings; changing them pauses the recording. Reports aggregate encode/decode means by operation count, counters over observed seconds, and peer window/rate values over observed connections. History adds per-connection windows/rates/failures and server compression savings/counts/p95 costs/captain transfers for the existing seven-day retention. Older saved timelines remain readable, with absent metrics unavailable.

To evaluate a change, compare the same world, player count and activity with one feature changed at a time. Check gameplay and connections alongside bandwidth, queues and CPU. Use [saved reports](REPORTS.md) for the comparison and [history](HISTORY.md) for individual incidents.

`serverImprovements.network` supplies optional replication-selection/priority counts, early-buffer counts/current bytes, the effective forced-sharing policy, negotiated map client count and map payload bytes/packets/skips/rejections. Counts and bytes cover the latest snapshot window; queued bytes and capable peers are gauges. Early buffering runs on clients, so a dedicated-server snapshot normally has zero buffer activity. To inspect client buffer counters, enable that client's JSON export and point a metrics service at it. Map byte counts exclude RPC/Steam overhead; transport traffic measurements cover total game traffic.

The dashboard's Server view displays these values. History and report comparisons include them, with missing values left unavailable for older snapshots. History storage upgrades to schema 4 and exported timelines to metric layout 3; earlier raw data, rollups and layout-1/2 reports remain readable. Deploy the updated metrics service with the mod to view the new counters.
