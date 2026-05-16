# Jeeber Location & Availability — Storage Design

Ticket: **T-database-007**
Scope:  `jeeber_availability` table (`migrations/0003_init_jeeber_availability.sql`)
        + the Redis hot-path key design that fronts it.

The Jeeber side of the marketplace has two storage requirements that pull in
opposite directions:

1. **Per-second location updates** from thousands of online drivers.
   Postgres can absorb this, but it pays for the durability we don't need at
   that frequency. Hot path lives in Redis.
2. **Durable presence + last-known location** that survives a Redis restart,
   feeds offline analytics, and serves cold lookups (`GET /jeebers/:id`).
   Postgres owns this.

The two stores are kept loosely consistent by a debounced flusher, and the
hot path is the source of truth for "who is online right now".

## 1. PostgreSQL — durable state

`jeeber_availability` (one row per Jeeber):

| Column          | Type                       | Notes                                                  |
|-----------------|----------------------------|--------------------------------------------------------|
| `user_id`       | `UUID PK`                  | FK → `users(id)`, cascade on delete                    |
| `is_online`     | `BOOLEAN`                  | Set by go-online / go-offline / stale sweeper          |
| `vehicle_type`  | `jeeber_vehicle_type` enum | `car`, `motorbike`, `bicycle`, `scooter`, `walk`       |
| `last_location` | `GEOGRAPHY(Point, 4326)`   | WGS84; metres-based distance math via `ST_DWithin`     |
| `last_seen_at`  | `TIMESTAMPTZ`              | Heartbeat watermark, used by the stale-online sweeper  |
| `created_at`    | `TIMESTAMPTZ`              |                                                        |
| `updated_at`    | `TIMESTAMPTZ`              | Maintained by the shared `set_updated_at` trigger      |

Indexes:

- `jeeber_availability_last_location_gix` — `GIST(last_location)`. Required
  for any `ST_DWithin` / `ST_Distance` lookup; GIST is the only viable
  index type for `GEOGRAPHY`.
- `jeeber_availability_online_vehicle_idx` — partial btree on `vehicle_type`
  where `is_online = TRUE`. Shrinks the candidate pool to the online subset
  before the GIST radius scan when matching by vehicle.
- `jeeber_availability_last_seen_idx` — partial btree on `last_seen_at`
  where `is_online = TRUE`. Lets the stale-online sweeper find lapsed
  heartbeats without a full scan.

Constraint:

```sql
CHECK (is_online = FALSE
       OR (last_location IS NOT NULL AND last_seen_at IS NOT NULL))
```

An online Jeeber must have at least one heartbeat behind them, otherwise
the matching service has nothing to match on.

### When Postgres is written

- **Go-online / go-offline / vehicle change** — written synchronously inside
  the request handler. These are infrequent and need to be durable.
- **Location heartbeat** — *not* written synchronously. The flusher (below)
  promotes the latest Redis position into Postgres on a debounce so the
  table stays warm enough to recover state after a Redis restart but does
  not eat the per-second write rate.

## 2. Redis — hot path

All Redis keys live under the `jeeber:` namespace. Names are stable; treat
them as part of the contract.

### `jeeber:online:geo` — global geo index

A Redis **GEO** sorted set (`GEOADD` / `GEOSEARCH`). Member is the Jeeber's
`user_id`; score is the GEO-encoded coordinate.

```
GEOADD jeeber:online:geo  <lon> <lat>  <user_id>
GEOSEARCH jeeber:online:geo  FROMLONLAT <lon> <lat>  BYRADIUS 3 km  ASC COUNT 50
```

This is the request hot path — when a customer opens the map or requests a
ride, the matching service does a `GEOSEARCH` against this set, then enriches
results from the per-Jeeber metadata hash below.

### `jeeber:online:geo:<vehicle_type>` — per-vehicle geo index

Same shape as the global set, but partitioned by vehicle:

```
jeeber:online:geo:car
jeeber:online:geo:motorbike
jeeber:online:geo:bicycle
jeeber:online:geo:scooter
jeeber:online:geo:walk
```

A Jeeber is added to **both** the global set and the set for their current
vehicle on every heartbeat, and removed from both on go-offline / TTL
expiry / vehicle change. Per-vehicle sets let the matching service skip
the post-filter step when the customer asks for a specific vehicle.

### `jeeber:meta:<user_id>` — per-Jeeber metadata hash

Persistent metadata that travels with each geo entry. Held as a hash so the
matching service can `HMGET` exactly the fields it needs:

```
HSET jeeber:meta:<user_id>
     vehicle_type   "car"
     last_seen_at   "2026-05-15T12:34:56Z"
     is_online      "1"
```

Lifetime is tied to the geo entry — when the Jeeber goes offline (explicitly
or via TTL sweep), the meta key is `DEL`-ed in the same pipeline.

### `jeeber:heartbeat:<user_id>` — TTL presence key

A short-lived sentinel updated on every heartbeat with `SET ... EX 30`
(30-second TTL). Its only job is to expire when the client stops checking
in, so the sweeper can detect lapsed Jeebers cheaply.

```
SET jeeber:heartbeat:<user_id> 1 EX 30
```

Choosing 30 s gives the mobile client three missed 10 s heartbeats before
we treat them as gone — tolerant of one bad cellular handoff, intolerant
of an app that has been backgrounded.

### Heartbeat write — atomic pipeline

A single `MULTI` / pipeline per heartbeat keeps the four keys in sync:

```
MULTI
GEOADD jeeber:online:geo                <lon> <lat>  <user_id>
GEOADD jeeber:online:geo:<vehicle_type> <lon> <lat>  <user_id>
HSET   jeeber:meta:<user_id>            last_seen_at <iso8601>
SET    jeeber:heartbeat:<user_id>       1  EX 30
EXEC
```

## 3. TTL strategy — stale-location cleanup

Redis TTLs only expire the `jeeber:heartbeat:<user_id>` key. They do not
remove the Jeeber from the geo sorted sets, because Redis sorted-set members
do not have per-member TTLs. A small sweeper closes that loop.

### Sweeper

| Field        | Value                                                                |
|--------------|----------------------------------------------------------------------|
| Owner        | Jeeb gateway team                                                    |
| Cadence      | Every **15 seconds** (continuous, not a cron)                        |
| Mechanism    | In-process background job inside the gateway, single-leader via Redis lock |
| Idempotency  | Yes — `ZREM` + `DEL` of an already-absent member is a no-op          |

On each tick the sweeper:

1. Reads the candidate set: `ZRANGE jeeber:online:geo 0 -1`.
2. For each `user_id`, `EXISTS jeeber:heartbeat:<user_id>`.
3. For every `user_id` whose heartbeat key is missing:
   - `ZREM jeeber:online:geo <user_id>`
   - `ZREM jeeber:online:geo:<vehicle_type> <user_id>` (vehicle pulled from the meta hash)
   - `DEL jeeber:meta:<user_id>`
   - Enqueue a Postgres update: `is_online = FALSE` for that `user_id`.

For a candidate set in the low tens of thousands the `EXISTS` round-trips
are cheap; if the population grows past ~100k online concurrently, swap
step 2 for an `MGET` over batched heartbeat keys.

### Postgres-side stale flip

Even with the Redis sweeper, the Postgres row should never lie about the
online state for long. A second, slower job runs every minute:

```sql
UPDATE jeeber_availability
   SET is_online = FALSE
 WHERE is_online = TRUE
   AND last_seen_at < NOW() - INTERVAL '2 minutes';
```

The `jeeber_availability_last_seen_idx` partial index keeps this scan
bounded to the currently-online subset.

### Why two layers

- The Redis sweeper exists so the next `GEOSEARCH` does not return ghosts.
  It must run on the order of seconds.
- The Postgres flip exists so cold consumers (admin dashboards, the
  matching service when Redis is unavailable) see the same truth. It can
  afford to lag by a minute.

## 4. Failure modes

| Scenario                                  | Behaviour                                                                                       |
|-------------------------------------------|-------------------------------------------------------------------------------------------------|
| Redis restart / data loss                 | All Jeebers appear offline until they next heartbeat. Postgres state is intact; the next heartbeat re-populates the geo sets. |
| Postgres unreachable on go-online         | Reject the request. We will not add a Jeeber to the geo set without a durable record of who they are. |
| Postgres unreachable on heartbeat         | Tolerated. Heartbeats only need Redis; the flusher catches up when the DB returns.              |
| Sweeper crashes / loses the leader lock   | Geo sets accumulate ghosts for a few seconds until another instance grabs the lock.             |
| Clock skew between gateway and Redis      | Affects `last_seen_at` interpretation only; the Redis TTL is server-side, so presence detection is not affected. |
