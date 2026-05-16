# Jeeb Gateway — Database

Canonical schema for **users**, **roles**, and **KYC submissions** consumed by
the Jeeb BFF aggregation layer.

## Layout

```
db/
  migrations/         # numbered, idempotent SQL migrations (incl. reference data)
  seeds/              # dev/CI-only seed scripts — never run in production
  apply.sh            # apply all pending migrations; pass --with-seed to also run seeds
  seed.sh             # apply dev/test seed scripts (refuses on production hostnames)
```

## Migrations are idempotent

Every migration is safe to re-run. We use:

- `CREATE TABLE IF NOT EXISTS …`
- `CREATE INDEX IF NOT EXISTS …`
- `DO $$ … END $$` guards for `CREATE TYPE` (Postgres lacks `IF NOT EXISTS` for types)
- `DROP TRIGGER IF EXISTS …` before `CREATE TRIGGER`
- `INSERT … ON CONFLICT DO NOTHING` into the `schema_migrations` ledger

This lets bootstrap, local-dev resets, and CI integration tests share the same
script without divergence.

## Applying migrations

```bash
# local dev (docker-compose brings up postgres on :5432)
export DATABASE_URL="postgres://jeeb:jeeb@localhost:5432/jeeb"
./db/apply.sh                 # schema + reference data only
./db/apply.sh --with-seed     # also load P1-P5 test accounts
```

`apply.sh` walks `db/migrations/*.sql` in lexicographic order and pipes each
file into `psql`. Files already recorded in `schema_migrations` are still
re-applied — they're no-ops thanks to the idempotency rules above — so the
script never has to track state itself.

## Reference data vs test data

| Source                          | Contains                                | Prod-safe |
|---------------------------------|-----------------------------------------|-----------|
| `migrations/0011_init_seed_reference_data.sql` | 5 delivery tiers, initial prohibited-items catalog | ✅ |
| `seeds/test_accounts.sql`       | P1–P5 persona users + demo KYC/addresses | ❌ dev/CI only |

`seed.sh` (and `apply.sh --with-seed`) refuses to run when `DATABASE_URL`
points at a host whose name contains `prod`, `production`, or `live`. Set
`FORCE_SEED=1` to override.

### Seed contents

* **Delivery tiers** (`delivery_tiers`) — five rows keyed by `code`:
  `flash` (30 min / 3 km / 15%), `express` (60 min / 7 km / 15%),
  `standard` (180 min / 15 km / 12%), `on_the_way` (no SLA / 25 km / 10%),
  `eco` (1440 min / 25 km / 10%). Commission rates encode BR-2.
* **Prohibited items** (`prohibited_items`) — 15 starter rows across the
  five categories called out in FR-17.1 (weapons, drugs, alcohol,
  prescription medication, hazardous materials), plus a small `other`
  bucket for live animals, cash, and human remains. `alcohol` rows are
  inserted with `active = FALSE` and admins enable per-market.
* **Personas** (`users` + `kyc_submissions` + `saved_addresses`) — five
  fixed-UUID accounts so tests can reference them by literal value:
  P1 Layla, P2 Hajj Antoine (Arabic), P3 Rami (dual-role), P4 Khaled
  (Jeeber power user, approved KYC), P5 Ops Admin. Phone numbers use the
  `+961 70 0000XX` range that no Lebanese carrier issues to real
  subscribers.

## Schema overview

| Table                       | Purpose                                                       |
|-----------------------------|---------------------------------------------------------------|
| `users`                     | Identity — phone, email, name, avatar, roles (JSONB array)    |
| `kyc_submissions`           | KYC lifecycle — documents (JSONB), status enum, reviewer      |
| `chat_messages`             | Conversational layer — text/image/voice/location/system/offer |
| `jeeber_availability`       | Driver online state + last location (GEOGRAPHY)               |
| `delivery_tiers`            | Five-tier catalog (flash/express/standard/on_the_way/eco)     |
| `delivery_requests`         | Request lifecycle, pickup/dropoff (GEOGRAPHY), status FSM     |
| `offers`                    | Jeeber bids on a request — fee, ETA, status (auction)         |
| `prohibited_items`          | Admin-moderated catalog of disallowed items (active/CRUD)     |
| `admin_actions`             | Append-only audit log of admin mutations across entities      |
| `notification_preferences`  | Per-user per-category notification opt-in flags               |
| `delivery_financials`       | Per-delivery money trail — goods, fee, commission, payout     |
| `settlement_batches`        | Weekly Jeeber payout batches — totals, method, status         |
| `ratings`                   | Two-sided 1-5 star ratings per delivery, blind-reveal model   |
| `disputes`                  | Admin-handled dispute escalations on a delivery (FR-13)       |
| `schema_migrations`         | Applied-migration ledger                                      |

### Admin moderation & audit

`prohibited_items` holds the moderated catalog the client app reads when
composing a request. Entries are soft-disabled via `active = FALSE` rather
than deleted so `admin_actions` rows keep a stable anchor.

`admin_actions` is INSERT-only by convention: every admin mutation across the
system writes one row with `before_state` / `after_state` JSONB snapshots so
dashboards can diff without joining back to a moving source.

### Notification preferences

`notification_preferences` is normalised: one row per `(user_id, category)`.
The toggleable categories are `offers`, `chat`, `status_changes`,
`rating_reminders`. Critical channels (`otp`, `system_critical`) are
always-on and live in application defaults — they are NOT modelled in this
table and the API rejects any attempt to disable them.

### Role model

`users.roles` is a JSONB array of role strings, e.g. `["customer","driver"]`.
A GIN index supports `WHERE roles @> '["driver"]'` lookups. Centralising role
membership in one column (rather than a join table) keeps the BFF's identity
read path single-query.

### KYC lifecycle

`kyc_status` enum: `pending → in_review → approved | rejected | expired`.
A partial unique index (`kyc_one_active_per_user`) guarantees a user can have
at most one open submission at a time.

### Offers (reverse auction)

`offers` holds the bids Jeebers submit against a matched `delivery_requests`
row (FR-6.*). A unique index on `(request_id, jeeber_id)` enforces one bid
per Jeeber per request — the offer-service treats it as an upsert key.

`offer_status` enum: `pending → accepted | rejected | withdrawn`. The state
machine is enforced at the application layer (offer-service); the enum
constrains the value domain only. Three indexes back the hot paths:
`(request_id, created_at DESC)` for the Client's "see all bids" view,
`(jeeber_id, created_at DESC)` for the Jeeber dashboard, and a partial
`(request_id) WHERE status = 'pending'` for the auction-expiry sweep.

### Financial ledger

`delivery_financials` is the per-delivery money trail: `goods_cost`,
`delivery_fee`, the `commission_rate` snapshot, the computed
`commission_amount`, and a generated `jeeber_payout` column
(`delivery_fee − commission_amount`). One row per delivery, enforced
by a unique index on `delivery_id`. Earnings aggregation rides on
`(jeeber_id, created_at DESC)`; the batch builder picks unsettled rows
via a partial index `(jeeber_id, created_at) WHERE settlement_batch_id
IS NULL`.

`settlement_batches` rolls a Jeeber's commission into a weekly payout.
`(jeeber_id, period_start, period_end)` is unique so a period is opened
exactly once. `payout_method` is `bank_transfer | mobile_wallet | cash`;
`settlement_status` is `pending → processing → paid | failed |
cancelled`. The actual disbursement goes through
`unified_payment_gateway` (locked-in payments policy) — the
`external_reference` column stores the gateway transaction id.

### Ratings (blind reveal)

`ratings` is the source of truth for post-delivery 1-5 star feedback
(FR-10.*). Both sides of a delivery may rate each other once;
`(delivery_id, rater_id)` is unique. `ratee_id` is denormalised from
the delivery so aggregate-rating queries hit a single index
(`ratings_ratee_created_idx`) without joining `delivery_requests`.

Blind reveal (FR-11.*): rows are written with `revealed_at = NULL`;
the score-taking-service stamps `revealed_at = NOW()` on **both** rows
of a delivery once the second party rates, or on any remaining
unrevealed row after the 7-day post-delivery boundary. The BFF
filters `WHERE revealed_at IS NOT NULL` before returning a rating.
The denormalised `users.rating` / `users.rating_count` columns (from
0006) are the read path; this table is the audit trail.

### Disputes (FR-13)

`disputes` holds admin-handled escalations against a delivery.
`category` is an enum (`item_not_delivered`, `item_damaged`,
`wrong_item`, `late_delivery`, `payment_issue`, `harassment`, `fraud`,
`other`). `status` is `open → in_review → resolved | rejected |
cancelled`; the state machine is enforced at the application layer.
Once an admin claims a dispute the row carries `admin_id`, and
terminal `resolved` rows must record both `resolution` text and
`resolved_at`. A partial unique index keeps at most one open dispute
per `(delivery_id, reporter_id)` — re-filing the same complaint is a
no-op upsert.
