# Database Backup & Recovery — RETIRED

**Status:** RETIRED at W5-13, 2026-08-16. jeeb-gateway owns no database, so this
service has nothing to back up. Do not follow the procedure that used to be here.

## Why

The `GatewayPostgres` and `WalletPostgres` seams were deleted in the gwdbx
programme (gateway PRs #445 / #446 / #448). `src/` now contains no Npgsql or
EntityFrameworkCore reference and no `Postgres*.cs` file, and
`scripts/gateway-db-seam-allowlist.txt` is empty so any new seam is rejected.

The `db/` directory went with them. **Every script the old runbook told an
operator to run is absent from this repository** — `db/backup/pg_backup.sh`,
`db/backup/pg_wal_archive.sh`, `db/backup/pg_restore.sh`,
`db/backup/verify_backup.sh`. Following it during an incident would have burned
the RTO on commands that cannot execute. The deletion ledger scheduled this
rewrite for W5-11 (`docs/runbooks/gwdbx-deletion-ledger.md` §8) and it did not
happen then; W5-13 is clearing it.

## Where the responsibility went

Backup and recovery for the data that used to live in `jeeb_gateway` belongs to
whichever service now owns that data:

| Data | Owner |
|---|---|
| Requests, deliveries, tier catalog | delivery-service |
| Balances, transaction ledger | wallet-service |
| Settlements, batches, settlement ledger | settlement-service |
| Idempotency, locks, work-items, audit events, published config | jeeb-state-service |
| Identity, roles, profiles | user-management |
| Saved locations, notification preferences | remote-user-preferences |

Each of those has its own retention, verification and drill obligations. This
repository states none of them, because it can no longer verify any of them.

## Two things that are still true and easy to get wrong

- **The `jeeb_gateway` database is NOT being dropped** (owner directive
  2026-08-16). It is retained, unread by this service, holding roughly 64
  orphaned rows by decision. Nothing here authorises a `DROP DATABASE`.
- **`docker-compose.yml` and `docker-compose.backup.yml` still define a
  `postgres:16-alpine` service, a `postgres-backup` sidecar and a
  `ConnectionStrings__Default` for the gateway.** Nothing in `src/` reads any of
  it, and `docker-compose.backup.yml` bind-mounts `./db/backup/`, which does not
  exist — so the overlay cannot start. It is inert local-compose residue, not
  evidence of a gateway database. Config cleanup is out of this document's scope;
  it is recorded here so the mismatch does not re-seed the belief.
