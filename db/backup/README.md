# Jeeb Gateway — Database Backup

Automated PostgreSQL backups + point-in-time recovery for the gateway's
identity / KYC database. Operator-facing runbook lives at
[`docs/runbooks/db-backup-and-recovery.md`](../../docs/runbooks/db-backup-and-recovery.md).

## Files

| Script              | Role                                                            |
|---------------------|-----------------------------------------------------------------|
| `pg_backup.sh`      | Cron job — `pg_dump` to `BACKUP_DIR`, write sha256, prune, alert|
| `pg_wal_archive.sh` | `archive_command` for PITR                                      |
| `pg_restore.sh`     | Operator tool — logical restore OR PITR replay                  |
| `verify_backup.sh`  | CI / on-demand restore-into-throwaway-PG smoke test             |
| `Dockerfile`        | Sidecar image (postgres-client + busybox crond)                 |
| `cron/crontab`      | `0 */6 * * *` schedule                                          |
| `cron/entrypoint.sh`| Initial backup on boot, then hand off to crond                  |

## Quick check (local)

```bash
# Bring up postgres + the backup sidecar:
docker compose -f docker-compose.yml -f docker-compose.backup.yml up -d

# Force a backup immediately:
docker exec jeeb-postgres-backup /opt/jeeb-backup/pg_backup.sh

# List dumps:
docker exec jeeb-postgres-backup ls -lh /var/backups/jeeb/
```

## Acceptance (T-database-010)

- [x] Automated backup every 6h — `cron/crontab`
- [x] PITR tested + documented — WAL archive_command + runbook §4
- [x] Backup monitoring with alert on failure — `ALERT_WEBHOOK_URL` POST
- [x] Recovery procedure in runbook — `docs/runbooks/db-backup-and-recovery.md`
