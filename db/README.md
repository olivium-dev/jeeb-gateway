# Jeeb Gateway — Database

Canonical schema for **users**, **roles**, and **KYC submissions** consumed by
the Jeeb BFF aggregation layer.

## Layout

```
db/
  migrations/         # numbered, idempotent SQL migrations
  apply.sh            # apply all pending migrations to $DATABASE_URL
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
./db/apply.sh
```

`apply.sh` walks `db/migrations/*.sql` in lexicographic order and pipes each
file into `psql`. Files already recorded in `schema_migrations` are still
re-applied — they're no-ops thanks to the idempotency rules above — so the
script never has to track state itself.

## Schema overview

| Table              | Purpose                                                   |
|--------------------|-----------------------------------------------------------|
| `users`            | Identity — phone, email, name, avatar, roles (JSONB array)|
| `kyc_submissions`  | KYC lifecycle — documents (JSONB), status enum, reviewer  |
| `schema_migrations`| Applied-migration ledger                                  |

### Role model

`users.roles` is a JSONB array of role strings, e.g. `["customer","driver"]`.
A GIN index supports `WHERE roles @> '["driver"]'` lookups. Centralising role
membership in one column (rather than a join table) keeps the BFF's identity
read path single-query.

### KYC lifecycle

`kyc_status` enum: `pending → in_review → approved | rejected | expired`.
A partial unique index (`kyc_one_active_per_user`) guarantees a user can have
at most one open submission at a time.
