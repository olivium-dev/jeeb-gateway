#!/usr/bin/env bash
# Run a startup backup, then hand off to crond for the 6h schedule.
set -euo pipefail

: "${DATABASE_URL:?DATABASE_URL must be set}"

mkdir -p /var/log /var/backups/jeeb
touch /var/log/jeeb-backup.log

# Initial backup on container start — establishes a recovery point
# immediately so a crash before the first cron tick doesn't leave us empty.
echo "[$(date -u +%FT%TZ)] startup backup" >> /var/log/jeeb-backup.log
/opt/jeeb-backup/pg_backup.sh >> /var/log/jeeb-backup.log 2>&1 || \
    echo "[$(date -u +%FT%TZ)] startup backup FAILED (cron will retry)" >> /var/log/jeeb-backup.log

# busybox crond: -f foreground, -d 8 log level, -L stdout
exec crond -f -d 8 -L /dev/stdout
