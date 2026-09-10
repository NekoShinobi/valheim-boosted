# Stages 3–5: experimental server improvements

These independently switched features extend the Stage 2 scheduler. **All four new `Enabled` settings default to `false`.** They require the reviewed Valheim 1.0.7 / protocol 39 contracts. Builds, managed policies, real Harmony transforms and reference checks pass; live Steam multiplayer performance and gameplay acceptance remain pending.

## Enable an experiment

Stop Valheim, edit its generated `valheim.boosted.cfg`, and restart. On the community server image the persistent file is `/config/bepinex/valheim.boosted.cfg`. Keep `[Telemetry] Enabled = true`. Existing saved settings are preserved.

```ini
[SendWindows]
Enabled = false
TargetBytesPerSecond = 153600
MaximumBytes = 32768

[SteamRate]
Enabled = false
MaximumBytesPerSecond = 307200

[CaptainOwnership]
Enabled = false

[Compression]
Enabled = false
EncodeBudgetMs = 1
```

Change the relevant `Enabled` to `true` for a trial. Send windows, Steam maximum-rate changes and captain assignment apply on the **dedicated Steam server**; clients may remain unmodded. Compression requires enabling it on **both endpoints** and successful per-connection negotiation. It supports Steam clients and player hosts as well as dedicated servers. No additional game ports, native libraries or dashboard control endpoint are added.

The four switches are independent of `Scheduling.Enabled`. For a vanilla replication baseline, disable scheduling and these four switches, leaving diagnostics enabled. For a Stage 2 baseline, leave scheduling on. Use the dashboard's **Reset stats**, **Save report**, **Compare reports** and **History** to compare one experiment at a time. The dashboard displays measurements; config changes require a game restart.

## Stage 3: per-peer allowances and Steam maximum rate

`SendWindows` replaces only the two reviewed 10,240-byte allowance constants in `ZDOMan.SendZDOs`. It preserves object discovery, serialization, revisions, force-send lists, destroy processing and the existing queue measurement. The allowance is a budget for constructing a batch; a single serialized object can exceed it. It is not Steam's send-buffer capacity or a hard wire-packet size.

Once per second, each ready Steam peer gets a bounded allowance calculation using RTT, Steam's current send-rate estimate and **unsent** queue signals. After three healthy observations, the target is `min(configured ceiling, estimated rate) × RTT + 4096`, clamped between 10 KiB and the configured maximum (32 KiB by default; 64 KiB absolute limit). Growth is limited to 2 KiB per observation. RTT contributes at most one second to the calculation.

Two consecutive observations of queue pressure halve the allowance toward the vanilla floor. Pressure means estimated queue delay above 100 ms, a nonempty managed application queue, or pending reliable bytes above `max(10 KiB, estimated rate / 10)`. Already-sent unacknowledged bytes are not used as evidence of unsent congestion. Missing, invalid or over-three-second-old measurements restore the vanilla allowance. Explicit flushes retain the vanilla allowance; client, player-host and non-Steam send paths do too. No client-reported FPS enters this policy.

`SteamRate` separately raises a ready connection's `SendRateMax` to the requested maximum (300 KiB/s by default), preserving a higher existing limit and never writing `SendRateMin`. It does **not** promise that throughput will reach this ceiling. Steam retains its congestion control and the game's existing minimum rate. The adapter chooses `SteamNetworkingUtils` or `SteamGameServerNetworkingUtils` from the running socket's reviewed send interface, checks wrapper signatures and initialization guards, reads current maximum/minimum and inheritance, writes only the connection scope, and verifies readback.

An inherited setting is restored by removing the connection override; an explicit setting is restored to its prior value. This follows Valve's [connection configuration API](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/include/steam/isteamnetworkingutils.h). Readback failures trigger restoration; failed restoration is reported, with up to three attempts while the connection remains tracked. A different value or a cleared override indicates another writer, so the mod stops managing that connection's maximum. Identical writes by another mod cannot be distinguished. A native API failure can prevent restoration until the connection closes; diagnostics do not claim a successful rollback in that case. Global settings and buffer sizes are never written.

## Stage 4: ship captain ownership

The dedicated server examines at most 64 peer entries once per second, using replicated ZDOs already in memory. A candidate must have a ready, connected Steam peer; own the character ZDO associated with that peer; match the ship's **granted** helm-user player ID; be attached to that ship through `SyncTransform`; have a replicated relative position within 1.5 m of the prefab's helm attachment; and have the ship inside the peer's active area. Ambiguous candidates are skipped.

The allowlist is `Raft`, `Karve`, `VikingShip` and `Drakkar`; the registered prefab must actually expose a `Ship` and helm attachment. Unknown or modified layouts can fail eligibility. Prefab data is inspected at runtime without creating ship instances or loading distant zones. This does not establish that every asset variant has passed gameplay testing.

Eligibility must persist for one second. Transfers have a five-second cooldown, including brief dismounts, and at most two ships transfer per scan. At most 128 policy records are retained; unused records expire after ten seconds. A transfer calls the existing `ZDO.SetOwner` and forces replication of that ZDO. Unowned ships, disconnected/detached captains and already-correct owners are left to vanilla handling. Disabling the feature stops further assignments; it does not undo an otherwise valid owner assignment.

The grant/release, player-relative-position, transform-sync, ship-owner-update and owner-write methods require an exact reviewed client or dedicated fingerprint set. A Harmony patch on these methods blocks captain assignment; periodic checks detect later changes. The RPC response sender is the previous simulation owner, so it is deliberately not used as captain identity.

This aligns a ship with the person steering it. It does not choose the strongest computer, move NPC simulation to the dedicated server, or rebalance general world ownership. Other interactables and NPCs remain outside Stage 4's implemented scope.

## Stage 5: negotiated lossless ZDO compression

Only normal `ZDOData` payload dispatch is eligible. The vanilla serializer first produces its unchanged byte sequence; an independent Deflate frame may then be sent through `valheim.boosted.zdo.v1`. The receiver reconstructs the exact original bytes and calls the reviewed vanilla `RPC_ZDOData`. Routed gameplay RPCs, handshakes, zone discovery and destruction RPCs keep their existing format.

`valheim.boosted.compression.v1` carries a 29-byte offer/acknowledgment/stop message: protocol version 1, Deflate algorithm 1, dictionary ID 0 (none), maximum raw size 65,536 bytes, and a random token for that sending connection. Receiving an offer installs decode readiness; a sender must receive an acknowledgment echoing its own token before sending compressed data. Each direction agrees independently. Each connection permits three staggered offer attempts and 32 received control messages. Unmodded, disabled, unsupported or silent peers continue using vanilla ZDO messages. Reconnection creates new tokens and negotiation state.

The data envelope contains a 16-byte sender token, a 20-byte header with magic/version/algorithm/dictionary, exact raw and compressed lengths, and CRC32, followed by Deflate data. Raw batches smaller than 512 bytes or larger than 64 KiB bypass compression. Compression must save more than 64 bytes and at least 5% after the envelope. CRC32 detects corruption; it is not an authentication mechanism. Connection identity and reliable ordered delivery come from the existing Steam RPC transport.

Encoding runs synchronously on the game thread with a default **1 ms soft budget per frame across peers**. One bounded batch can exceed that budget; subsequent batches use vanilla. Decode allocation is limited to 64 KiB per message. These are bounded operations, not a hard total CPU guarantee. The existing game receive loop handles message scheduling. There are at most 64 negotiated connections. No separate queues or mutable compression dictionaries are introduced.

Dispatch occurs once, after the final bytes are chosen. Steam's existing send queue retries those bytes unchanged; the mod never re-encodes a queued frame or repeats a partially committed ZDO send. Small/incompressible/over-budget payloads and encoding failures use vanilla sends. A runtime fallback stops compressed sending and asks peers to stop, while retaining decoders for frames already queued. Hot-unloading the decoder disconnects negotiated receiving peers so they cannot keep sending to an absent handler; use normal restarts for deployment.

Malformed or unnegotiated compressed payloads close the offending connection. Silently dropping them would lose already-committed sender revision state, and interpreting their framing as vanilla would be incorrect. A failed capability offer alone does not disconnect an unmodded or incompatible peer. Compression checks the receive-method contract and blocks overlapping foreign receive hooks; Stage 2/3/5 share the reviewed send pipeline and its conflict fallback.

## Observability and acceptance

The snapshot adds optional `serverImprovements` and `peers[].improvements`. **Server improvements** on the overview shows configured switches, actual feature statuses, per-peer allowances, requested/original/effective Steam limits, write failures, captain transfers, compression agreement, savings and encode/decode timings. Timing windows without work remain null. Compression counters reset at snapshot capture; rate failures are cumulative for the connection.

Saved report metadata includes all new settings; changing them pauses the recording. Reports aggregate encode/decode means by operation count, counters over observed seconds, and peer window/rate values over observed connections. History adds per-connection windows/rates/failures and server compression savings/counts/p95 costs/captain transfers for the existing seven-day retention. No client telemetry wire format or player-identity change is needed. Database schema 3 appends metric layout 2 without deleting samples or guessing values for old rows. Layout-1 saved timelines remain readable, with absent metrics unavailable.

Automated checks cover window pressure and missing metrics, rate readback/restore/external writers, captain eligibility/dwell/cooldown, exact compression roundtrips and malformed lengths/checksums, bounded negotiation, reconnect tokens, drain behavior, real Harmony transformations on client and server assemblies, report aggregation and history migration. The headless Mono transform check can print a missing `UnityEngine.Time` internal-call warning when compiling the real game method; it does not execute Unity simulation or native Steam calls.

Live acceptance still requires a disposable multiplayer world: compare 2/4/8/10-player workloads, then include a constrained connection, joins/teleports, a large build, save/flush, reconnect, captain swaps/passenger embark/disembark, ship destruction and shutdown. For compression, mix enabled, disabled and unmodded peers and check interactions/world state alongside bytes and CPU. Verify actual Steam rate readback and restoration on the deployed native library. A lower byte count or higher FPS alone is not evidence that replication and gameplay remain correct.
