# Player telemetry and persistent history

The metrics service retains **seven days** of server and client samples by default. Open **History** to align player frame intervals with server frame intervals, RTT from both endpoints, replication delays, and player lag markers. The mod must be installed on a client to collect its performance measurements; unmodded clients can still connect normally.

## Enable and retain it

The mod generates these defaults in `valheim.boosted.cfg`:

```ini
[ClientTelemetry]
ShareWithServer = true
ReceiveFromClients = true
IncludePlayerNames = true
MarkLagKey = F9
```

Restart the game or server after changing them. `ShareWithServer` controls outgoing client measurements, `ReceiveFromClients` controls server reception, and `IncludePlayerNames` controls server-known character names in exported telemetry. With names disabled, the dashboard uses server-scoped player IDs, or session IDs where account identity is unavailable. Disabling sharing does not disable the client's existing local HUD or local JSON export. A capability handshake can still advertise that sharing is disabled; it contains the mod build ID and sharing flag, but no performance measurements.

### Returning players and login sessions

History defaults to **Combine returning players across sessions**. The same Steam account keeps one player identity when it reconnects, changes characters, or returns after a server restart. Client and server-observed connection measurements remain separate perspectives. Uncheck this option to inspect individual login series; **Player sessions** shows each character, first observation, last observation and end status. The range summary combines the selected player's observed data across logins and excludes offline gaps.

The mod generates a private, random `ClientTelemetry.IdentityKey` in the server's config on first use. **Preserve that config across server restarts and container replacement.** On the community image this is `/config/bepinex/valheim.boosted.cfg`. Do not copy an example key or publish the generated one. Changing or losing it creates new player identities; existing history remains readable but will no longer group with new measurements. An invalid key disables account grouping and is reported in client telemetry status.

The server derives a pseudonymous player ID using HMAC-SHA256 over the Steam account obtained from the authenticated, ready connection. Neither the key nor the raw account ID enters snapshots, history or reports. Identity is server-scoped; character names are labels and never determine grouping. The new fields require the updated **server mod and metrics service**; the client RPC protocol stays at version 1. Session/connection identity is available for unmodded peers too, but their client performance measurements remain unavailable. Disabling performance sharing does not erase server-observed connections or existing history.

Each connection has a fresh stream ID, even when the game reuses a peer ID or the client resets its sequence counter. Disconnected session metadata remains in the bounded export buffer for up to 60 seconds (128 ended plus 64 active sessions), allowing the metrics reader to record endings. If the reader misses a disconnect or sees a different server/world, it records an **estimated** ending at the last observed time. A stopped snapshot closes open sessions at the stop observation. A stale export leaves the last state unknown; it cannot establish an exact disconnect time. Sessions entirely missed while metrics was offline cannot be reconstructed.

Stable account grouping currently applies to Steam sockets. Other transports and older history without identity retain separate session series. The schema migration preserves existing measurements; it never attempts to join users by name, peer ID or IP. Old saved archives remain readable.

The metrics container uses these settings:

```yaml
environment:
  TELEMETRY_PATH: /telemetry/snapshot.json
  REPORTS_DIRECTORY: /reports
  HISTORY_RETENTION_DAYS: "7"
volumes:
  - /your/server/config/valheim-boosted/telemetry:/telemetry:ro
  - valheim-boosted-reports:/reports
```

Use the complete run command in the [README](../README.md#start-the-metrics-server), or [Compose example](../docker/compose.metrics.yml). The reports mount must be writable and persistent. The default database is `/reports/history.sqlite`; `HISTORY_DATABASE_PATH` can override it. SQLite's adjacent `-wal` and `-shm` files also need writable storage. Use one metrics writer per database on local storage. No separate database service or client HTTP port is needed.

`HISTORY_RETENTION_DAYS` accepts whole numbers from 1 to 365. Restart the metrics service to change it. Reducing retention expires older samples; increasing retention cannot recover deleted history. History begins when this version of the metrics service first receives fresh snapshots. Existing saved reports do not gain past client data retroactively.

## Collection and transport

The client reuses the existing telemetry collector, whose default sampling window is one second. Each completed window records:

| Group | Measurements |
| --- | --- |
| Frames | Frame count, mean, p95, p99, maximum, time of the worst frame, and counts at least 50/100/250 ms |
| Process | CPU percentage where 100% equals one core, managed memory, and GC collection counts by generation |
| Networking | ZDO update p95, RTT, estimated transport queue delay, and changed-ZDO backlog when available |
| Context | Window focus, local-player availability, configured frame limit, VSync count, and an optional lag marker |

Frame intervals include waiting, VSync and frame limiting. They are not GPU execution time or simulation CPU duration. Existing bounded percentile samples are reused; long windows can have fewer percentile samples than observed frames. Missing or disabled source metrics remain null, rather than becoming zero. CPU measurements can exceed 100% on multiple cores. Local-player availability is a loading hint, not a definitive loading-screen detector.

There are no additional Harmony hooks for this transport. A namespaced direct `ZRpc` handler is registered on ready peers after compatibility checks. A capability exchange negotiates protocol version 1; a client makes at most three hello attempts. A missing or incompatible peer never becomes a required mod dependency. Telemetry rejection never intentionally disconnects the game peer.

Default transport limits are fixed in the mod:

| Limit | Behavior |
| --- | --- |
| Send interval | At most one sample batch every two seconds per client |
| Batch | Up to five fixed-field binary windows, with the application RPC packet bounded to 1 KiB including its framing |
| Backlog | At most 60 windows, with windows older than 60 seconds discarded before sending |
| Reception | At most 64 tracked peers; 1 KiB/s token refill and a 4 KiB burst allowance per peer; tiny messages cost at least 256 bytes |
| Recent server export | At most 512 received windows, retained for up to 60 seconds to tolerate dashboard polling delays |
| Session metadata | Up to 64 active and 128 ended sessions; ended metadata expires after 60 seconds |
| Lag markers | One marker per window, with a five-second input cooldown |

The steady-state payload is a few hundred bytes per two-second batch at default sampling, plus occasional clock exchanges and transport overhead. Backlogs drain at the same bounded cadence. Sending pauses when the managed game queue is nonempty, pending reliable bytes exceed 64 KiB, or the cached estimated queue delay exceeds 250 ms. This avoids adding telemetry during known congestion; it cannot guarantee delivery or detect every form of congestion. Dropped samples and congestion skips are exported as counters.

All strings and batch sizes are bounded. The server derives peer identity from the receiving game connection, validates sample fields and increasing sequence numbers, and rejects a malformed batch before retaining any of its windows. Clients cannot submit another player's identity. These remain self-reported diagnostic measurements, not trusted inputs for gameplay decisions. No hardware serials, Steam IDs, IP addresses, or per-object ownership lists are added to this protocol.

## Shared time and correlation

Client windows use a monotonic clock. Every 30 seconds, the server attempts a four-timestamp exchange to estimate the offset from the client clock to the server clock, subtracting client processing time from the measured round trip. It prefers a lower-delay estimate and refreshes old estimates. Each sample stores:

- The window's start and end mapped to server UTC.
- The server receipt time, kept separately from when the measurements occurred.
- Clock uncertainty: half the exchange delay plus a 200 ppm allowance for drift.
- The raw client monotonic window, sequence, and optional event times in the mod export.

Clock estimates expire after two minutes. Samples without a current estimate are dropped and synchronization is retried; their receipt time is never substituted as their occurrence time. Strong path asymmetry, clock drift beyond the allowance, or long stalls can still reduce alignment accuracy. The server UTC axis is anchored to its system clock at mod startup and advanced monotonically; keep the server and metrics host clocks synchronized. A wall-clock correction during a run does not redefine already recorded sample times.

Press **F9** to mark a lag incident. The timestamp is when the client processes the input; after a freeze, that may be later than the perceived incident. Clicking a marker or a chart inspects the same time bucket across the selected perspectives. Sample counts, observed duration, and clock uncertainty help distinguish an actual observation from a gap. Long-range buckets show aggregates and cannot establish sub-second ordering.

Compare client performance, server frame intervals, RTT and replication activity around that time. Focus loss, frame caps, loading, GC and a shared busy scene can explain coincident slowdowns. Correlation alone does not identify who caused lag, and this version does not collect simulation ownership or nearby-player context.

## Persistent data model

SQLite runs in a Bun worker in the metrics service, outside both Valheim and the HTTP event loop. The HTTP process allows one in-flight history ingestion plus one pending snapshot; newer snapshots replace the pending offer if storage falls behind. This bounds backlog memory and exposes an offer-replacement counter. The mod's recent-client buffer tolerates some missed polls, while missing server snapshots remain gaps.

| Table | Identity and contents |
| --- | --- |
| `series` | Hashed process/world/kind/source identity; perspective, player/session IDs, peer session, display name, build ID, configuration and compatibility metadata, first/last observation |
| `player_sessions` | One connection per process/world/stream; server-scoped player ID, character label, first/last observation, explicit or inferred ending |
| `samples` | Unique `(series_id, sequence)`; start/end/receipt times, clock uncertainty, marker/worst-frame time, and a fixed ordered metric array |
| `minute_samples` | Unique `(series_id, minute)`; aggregates, metric weights, observed duration and sample count |
| `history_meta` | Last completed rollup watermark; database format also uses SQLite `user_version` |

Server measurements and server-observed connections have separate series. Each client connection receives a fresh stream ID, so reconnecting client sequence numbers do not overwrite an earlier stream. A server process/world change also produces new series. Both client and server connection perspectives use that stream, with the game's peer ID retained as metadata. Legacy snapshots without stream metadata fall back to the old peer-based connection series. Repeated samples in successive JSON exports are deduplicated. The schema-2 migration adds identities and sessions in place and rebuilds derived minute summaries from retained raw samples to preserve fractional weighted averages. Schema 3 preserves that data and appends metric layout 2 for server improvements; existing 26-value rows and layout-1 archives remain valid. Missing appended metrics stay null, and old rollup weights contribute no observations to new metrics.

The database retains detailed samples for the entire configured retention period. Closed-minute summaries make wider queries cheaper; the last five minutes and partial range boundaries use detailed samples so delayed batches and boundary filtering remain accurate. A minute is assigned by window end, and aggregates retain observed durations rather than pretending every bucket has complete coverage.

Frame means are weighted by observed frames; FPS is observed frames divided by observed time. Counts are summed, memory and delay peaks use maxima, and window p95/p99 use the **maximum window percentile**. Those values are not the percentile of all frames in the selected range. Unavailable measurements are excluded from averaging, and missing time buckets remain gaps.

Cleanup runs at startup and approximately once a minute, including when no game is running. Queries exclude expired data immediately. SQLite reuses freed database pages, so the file need not shrink when rows expire. Retention bounds age, not an exact disk quota: storage scales with sampling rate, connected players, and row size. Monitor the history page's database size and free space on the reports volume. That size is allocated database pages and excludes the temporary WAL. SQLite uses WAL mode with `synchronous=NORMAL`; a machine power failure can lose recent uncheckpointed observations.

History queries return at most nine series (individual or grouped) and roughly 1,200 points per series. Series and session listings show up to 256 matches; narrow the range to find older sessions. Database failures appear in history status while the live dashboard and aggregate report functionality remain available. LayerChart renders the Svelte graphs with linked hover positions, clickable time selection and per-series legend controls; missing intervals stay empty and chart animation is disabled.

For a capacity example, a local synthetic run with ten reporting players, their ten server-observed connections and one server series produced 75,600 detailed rows over one hour of one-second windows. The database occupied about 21.9 MiB including minute summaries, extrapolating to roughly 3.6 GiB for seven continuously populated days with the same data shape. Actual values, connection churn, WAL activity and sampling configuration change that footprint. This is a sizing example, not a disk quota or a live-game performance benchmark.

## Reports and validation

**Reset stats** starts a new comparison recording without deleting history. **Save report** preserves the aggregate recording plus a bounded timeline archive of the matching server process/world. Saved timelines retain all supported metric columns, up to 64 series and about 2,400 aggregate rows; the server perspective is prioritized. They retain the first marker per series/bucket and expose their coarser resolution. Saved archives live in the report JSON and survive live retention cleanup. See [reports](REPORTS.md) for archive limits and comparison semantics.

Saved timelines also include session records and per-metric weights, so grouping several logins preserves correct averages. An archive remains scoped to its recording's server process/world. Older archives lack some weights and account IDs; their original individual series remain readable, and whole-range means that cannot be reconstructed accurately are left unavailable.

Managed tests exercise the actual binary transport using a queued pair of RPC fixtures, including clock alignment, malformed/replayed packets, congestion, feature switches, identity persistence and reconnect cleanup. The per-frame transport fast path is checked for managed allocations. Dashboard tests exercise real SQLite ingestion, weighted minute rollups and cross-login totals, name collisions, migration, retention, restart persistence and report archives. These checks do not replace profiling a live multiplayer session: the full collector, periodic serialization, Steam transport and database still have measurable costs.

## Server improvement history

The metric selector includes per-connection ZDO allowance (KiB), effective Steam maximum rate (KiB/s), cumulative rate failures (maximum across the range), and server compression bytes saved (KiB per bucket), frame counts, rejected messages, worst-window encode/decode p95 and ship transfers. Counters sum observed windows; timing percentiles use the largest window value, never an invented whole-range percentile. New fields are appended to the packed metric list so older values retain their indices. New archives declare `metricLayout: 2`; earlier archives remain readable. Server/series metadata also records experiment settings for attribution. See [Stages 3–5](SERVER-IMPROVEMENTS.md).
