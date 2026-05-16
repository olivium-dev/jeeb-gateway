# Database Backup & Recovery Runbook — Jeeb Gateway

**Service**: jeeb-gateway · Postgres 16
**Owner**: Platform on-call
**Targets**: RPO ≤ 6h (logical) / ≤ 5 min (PITR) · RTO ≤ 30 min

## 1. What is in place

| Layer        | Mechanism                                    | Schedule         | Where it lives                  |
|--------------|----------------------------------------------|------------------|---------------------------------|
| Logical dump | `pg_dump --format=custom` via `pg_backup.sh` | Every 6h (UTC)   | `db/backup/pg_backup.sh`        |
| WAL archive  | `archive_command` → `pg_wal_archive.sh`      | Continuous       | `db/backup/pg_wal_archive.sh`   |
| Verification | Restore into throwaway PG, assert tables     | Weekly + on PR   | `.github/workflows/db-backup-verify.yml` |
| Alerting     | `ALERT_WEBHOOK_URL` POST on success + fail   | Each run         | `pg_backup.sh` emits heartbeat  |

Retention: **168h (7 days) of 6h dumps + matching WAL segments**.

## 2. Alert response

### Alert: `pg_backup` failure webhook

Payload includes `host`, `timestamp`, `error`. Steps:

1. SSH to the backup host. `tail -200 /var/log/jeeb-backup.log` — find the failed run.
2. Re-run on demand: `docker exec jeeb-postgres-backup /opt/jeeb-backup/pg_backup.sh`.
3. If it fails again, treat as **P2**. Likely causes:
   - Disk full on `BACKUP_DIR` → free space, then re-run.
   - `pg_dump` cannot reach Postgres → check `DATABASE_URL`, network, credentials.
   - Postgres is itself unhealthy → escalate to on-call DBA before touching backups.

### Alert: missing heartbeat (no `status:ok` POST for >7h)

This means the cron didn't run, not that a single backup failed. Most often a sidecar crash-loop.

1. `docker ps | grep postgres-backup` — is it running?
2. `docker logs jeeb-postgres-backup --tail 200` — look for crond errors or pg_dump permission errors.
3. Restart: `docker compose -f docker-compose.yml -f docker-compose.backup.yml up -d postgres-backup`.

## 3. Logical restore (lost / corrupted database)

Use case: someone dropped a table, or the database is unrecoverable but lost data <6h is acceptable.

```bash
# 1. Stop writers (gateway + workers).
docker compose stop jeeb-gateway

# 2. Choose a backup. `latest.dump` is a symlink to the newest.
ls -lh /var/backups/jeeb/

# 3. Restore into a fresh DB. The script verifies the .sha256 sidecar.
TARGET_DATABASE_URL='postgres://jeeb:jeeb@postgres:5432/jeeb' \
  /opt/jeeb-backup/pg_restore.sh --dump /var/backups/jeeb/latest.dump

# 4. Restart writers.
docker compose start jeeb-gateway
```

Expected duration on the MVP dataset: **<5 min**. Watch for `pg_restore: error:` in stderr — the script exits non-zero on any restore error.

## 4. Point-in-time recovery (recover to an exact moment)

Use case: a bad migration ran at 14:32 UTC; we need state as of 14:30 UTC.

**Pre-reqs**: a `pg_basebackup` directory and the contents of `WAL_ARCHIVE_DIR`. The base backup is taken weekly via:

```bash
docker exec jeeb-postgres pg_basebackup \
    -D /var/backups/jeeb/basebackup-$(date -u +%Y%m%d) \
    -U jeeb -Fp -Xs -P
```

Recovery:

```bash
# 1. Stop the failing Postgres and move its data dir aside (don't delete).
docker compose stop postgres
mv /var/lib/postgresql/data /var/lib/postgresql/data.bad

# 2. Stage base + replay WAL up to target time.
export PGDATA=/var/lib/postgresql/data
/opt/jeeb-backup/pg_restore.sh --pitr \
    --base /var/backups/jeeb/basebackup-20260512 \
    --wal  /var/backups/jeeb/wal \
    --time '2026-05-15 14:30:00+00'

# 3. Bring Postgres back up. It will replay WAL and then promote.
docker compose start postgres

# 4. Tail the log until you see:
#    "recovery stopping before commit of transaction ..."
#    "database system is ready to accept connections"
docker logs -f jeeb-postgres
```

After promotion, **the cluster is on a new timeline**. The `data.bad` directory is your forensic copy — keep it until the post-mortem is closed.

## 5. Verification (do this; do not skip it)

Backups you have never restored are not backups.

- **Automated**: the `DB Backup Verify` workflow runs weekly and on every PR that touches `db/backup/`. It builds a backup and restores it into a fresh PG container; failure files a P2.
- **Quarterly manual drill**: a human operator runs `db/backup/verify_backup.sh` locally and logs the result in the platform ops doc. This catches regressions the CI matrix doesn't (e.g. host disk filling up).

If verification has not succeeded in the last 14 days, treat the next incident as **no backups exist** and plan accordingly.

## 6. Configuration reference

| Env var                  | Default                 | Purpose                                   |
|--------------------------|-------------------------|-------------------------------------------|
| `DATABASE_URL`           | _required_              | Source DB for `pg_backup.sh`              |
| `TARGET_DATABASE_URL`    | _required for restore_  | Destination DB for `pg_restore.sh --dump` |
| `BACKUP_DIR`             | `/var/backups/jeeb`     | Where dumps + checksums land              |
| `WAL_ARCHIVE_DIR`        | `/var/backups/jeeb/wal` | WAL segment archive                       |
| `BACKUP_RETENTION_HOURS` | `168`                   | Prune dumps older than this               |
| `ALERT_WEBHOOK_URL`      | _empty_                 | POST `{status,job,host,timestamp}` here   |

## 7. Known gaps

- Backups are local-volume only. Off-host replication (S3, GCS) is tracked in **T-database-011**; until that lands, a host loss = data loss.
- Base backups for PITR are manual weekly. Automating `pg_basebackup` is **T-database-012**.
- The alert webhook fans out via a single URL. Routing through PagerDuty is owned by the SRE team.
