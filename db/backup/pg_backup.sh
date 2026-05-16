#!/usr/bin/env bash
# Take a logical backup of the Jeeb gateway Postgres database.
#
# Output:
#   ${BACKUP_DIR}/jeeb-<UTC-ISO8601>.dump   (custom-format pg_dump)
#   ${BACKUP_DIR}/jeeb-<UTC-ISO8601>.sha256 (integrity checksum)
#   ${BACKUP_DIR}/latest.dump               (symlink to newest dump)
#
# Exit codes:
#   0  success
#   1  pg_dump or checksum failure -> alert webhook fires
#   2  configuration error         -> alert webhook fires
#
# Scheduled by db/backup/cron/crontab (every 6h).

set -euo pipefail

: "${DATABASE_URL:?DATABASE_URL must be set, e.g. postgres://user:pass@host:5432/db}"
: "${BACKUP_DIR:=/var/backups/jeeb}"
: "${BACKUP_RETENTION_HOURS:=168}"   # keep 7 days of 6h dumps
: "${ALERT_WEBHOOK_URL:=}"           # POST {status,error,host,timestamp} on failure

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
host="$(hostname -f 2>/dev/null || hostname)"
dump="${BACKUP_DIR}/jeeb-${stamp}.dump"
checksum="${BACKUP_DIR}/jeeb-${stamp}.sha256"
log_prefix="[pg_backup ${stamp}]"

alert() {
    local err="$1"
    echo "${log_prefix} FAIL: ${err}" >&2
    if [[ -n "${ALERT_WEBHOOK_URL}" ]]; then
        curl -fsS -X POST -H 'Content-Type: application/json' \
             --max-time 10 \
             -d "{\"status\":\"fail\",\"job\":\"pg_backup\",\"host\":\"${host}\",\"timestamp\":\"${stamp}\",\"error\":\"${err}\"}" \
             "${ALERT_WEBHOOK_URL}" || true
    fi
}

trap 'alert "unexpected error on line $LINENO"' ERR

mkdir -p "${BACKUP_DIR}"

if ! command -v pg_dump >/dev/null; then
    alert "pg_dump not installed"
    exit 2
fi

echo "${log_prefix} dumping to ${dump}"
pg_dump \
    --dbname="${DATABASE_URL}" \
    --format=custom \
    --compress=6 \
    --no-owner \
    --no-privileges \
    --file="${dump}"

sha256sum "${dump}" | awk '{print $1}' > "${checksum}"
ln -sfn "$(basename "${dump}")" "${BACKUP_DIR}/latest.dump"

# Prune dumps older than the retention window.
find "${BACKUP_DIR}" -maxdepth 1 -type f -name 'jeeb-*.dump' \
    -mmin +"$((BACKUP_RETENTION_HOURS * 60))" -delete
find "${BACKUP_DIR}" -maxdepth 1 -type f -name 'jeeb-*.sha256' \
    -mmin +"$((BACKUP_RETENTION_HOURS * 60))" -delete

bytes="$(stat -c%s "${dump}" 2>/dev/null || stat -f%z "${dump}")"
echo "${log_prefix} OK size=${bytes}B sha256=$(cat "${checksum}")"

# Heartbeat so monitoring can alert on *missing* runs, not just failed ones.
if [[ -n "${ALERT_WEBHOOK_URL}" ]]; then
    curl -fsS -X POST -H 'Content-Type: application/json' \
         --max-time 10 \
         -d "{\"status\":\"ok\",\"job\":\"pg_backup\",\"host\":\"${host}\",\"timestamp\":\"${stamp}\",\"bytes\":${bytes}}" \
         "${ALERT_WEBHOOK_URL}" || true
fi
