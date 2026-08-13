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
  `flash` (30 min / 3 km / 10%), `express` (60 min / 7 km / 10%),
  `standard` (180 min / 15 km / 10%), `on_the_way` (no SLA / 25 km / 10%),
  `eco` (1440 min / 25 km / 10%). Commission rates encode the flat USD policy.
* **Prohibited items** (`prohibited_items`) — 15 starter rows across the
  five categories called out in FR-17.1 (weapons, drugs, alcohol,
  prescription medication, hazardous materials), plus a small `other`
  bucket for live animals, cash, and human remains. `alcohol` rows are
  inserted with `active = FALSE` and admins enable per-market.
* **Personas** (`users` + `saved_addresses`) — five
  fixed-UUID accounts so tests can reference them by literal value:
  P1 Layla, P2 Hajj Antoine (Arabic), P3 Rami (dual-role), P4 Khaled
  (Jeeber power user), P5 Ops Admin. Phone numbers use the
  `+961 70 0000XX` range that no Lebanese carrier issues to real
  subscribers.

## Schema overview

| Table                       | Purpose                                                       |
|-----------------------------|---------------------------------------------------------------|
| `users`                     | Identity — phone, email, name, avatar, roles (JSONB array)    |
| `jeeber_availability`       | Driver online state + last location (GEOGRAPHY)               |
| `delivery_tiers`            | Five-tier catalog (flash/express/standard/on_the_way/eco)     |
| `delivery_requests`         | Request lifecycle, pickup/dropoff (GEOGRAPHY), status FSM     |
| `prohibited_items`          | Admin-moderated catalog of disallowed items (active/CRUD)     |
| `admin_actions`             | Append-only audit log of admin mutations across entities      |
| `notification_preferences`  | Per-user per-category notification opt-in flags               |
| `settlement_batches`        | Weekly Jeeber payout batches — totals, method, status         |
| `schema_migrations`         | Applied-migration ledger                                      |

### Dropped in gwdbx W0-08 (migrations 0045–0048)

`kyc_submissions`, `chat_messages`, `offers`, `delivery_financials`, `ratings`,
`disputes`, `jeeb_cancellation_strikes`, `partner_wallet_operations` and
`partner_otp_challenges` are gone from this database. Each was archived (W0-07)
before the drop and each authority now lives upstream: user-management (KYC),
Firebase `jeeb-5a293` (chat), offer-service (offers), wallet-service (money),
feedback-service (ratings), the generic case authority (disputes) and
jeeb-state-service (the partner idempotency KV).

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

### Financial ledger

`settlement_batches` rolls a Jeeber's commission into a weekly payout.
`(jeeber_id, period_start, period_end)` is unique so a period is opened
exactly once. `payout_method` is `bank_transfer | mobile_wallet | cash`;
`settlement_status` is `pending → processing → paid | failed |
cancelled`. The actual disbursement goes through
`unified_payment_gateway` (locked-in payments policy) — the
`external_reference` column stores the gateway transaction id.

### Ratings and disputes

Both moved upstream and their tables were dropped in W0-08. `users.rating` /
`users.rating_count` (from 0006) remain the gateway's denormalised read path,
written by the score-taking-service; feedback-service holds the rating rows and
the generic case authority holds escalations.
