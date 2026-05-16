#!/usr/bin/env bash
# Restore a Jeeb backup. Two modes:
#
#   1) Logical restore (default): pg_restore a .dump into TARGET_DATABASE_URL.
#        ./pg_restore.sh --dump /var/backups/jeeb/latest.dump
#
#   2) Point-in-time recovery: replay WAL up to RECOVERY_TARGET_TIME using a
#      base backup directory. Requires a base backup taken with pg_basebackup.
#        ./pg_restore.sh --pitr \
#            --base /var/backups/jeeb/basebackup \
#            --wal  /var/backups/jeeb/wal \
#            --time '2026-05-15 14:30:00+00'
#
# This script is the one operators run; the runbook references it directly.

set -euo pipefail

mode="dump"
dump=""
base=""
wal=""
target_time=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dump) dump="$2"; shift 2 ;;
        --pitr) mode="pitr"; shift ;;
        --base) base="$2"; shift 2 ;;
        --wal)  wal="$2"; shift 2 ;;
        --time) target_time="$2"; shift 2 ;;
        -h|--help)
            sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "unknown arg: $1" >&2; exit 2 ;;
    esac
done

if [[ "${mode}" == "dump" ]]; then
    : "${TARGET_DATABASE_URL:?TARGET_DATABASE_URL must be set for logical restore}"
    if [[ -z "${dump}" ]]; then
        echo "missing --dump <file>" >&2; exit 2
    fi
    if [[ ! -f "${dump}" ]]; then
        echo "dump not found: ${dump}" >&2; exit 1
    fi

    # Verify checksum if the sidecar .sha256 file is present.
    if [[ -f "${dump}.sha256" ]]; then
        expected="$(cat "${dump}.sha256")"
        actual="$(sha256sum "${dump}" | awk '{print $1}')"
        if [[ "${expected}" != "${actual}" ]]; then
            echo "checksum mismatch on ${dump}" >&2; exit 1
        fi
        echo "checksum OK"
    fi

    echo "restoring ${dump} -> TARGET_DATABASE_URL"
    pg_restore \
        --dbname="${TARGET_DATABASE_URL}" \
        --clean --if-exists \
        --no-owner --no-privileges \
        --exit-on-error \
        --jobs=4 \
        "${dump}"
    echo "logical restore complete"
    exit 0
fi

# --- PITR mode ---
if [[ -z "${base}" || -z "${wal}" || -z "${target_time}" ]]; then
    echo "PITR requires --base, --wal, --time" >&2; exit 2
fi
: "${PGDATA:?PGDATA must point to the cluster data dir for PITR}"

if [[ -e "${PGDATA}" && -n "$(ls -A "${PGDATA}" 2>/dev/null)" ]]; then
    echo "${PGDATA} is not empty; refusing to overwrite. Move it aside first." >&2
    exit 1
fi

echo "staging base backup from ${base} -> ${PGDATA}"
mkdir -p "${PGDATA}"
cp -a "${base}/." "${PGDATA}/"

cat > "${PGDATA}/postgresql.auto.conf" <<EOF
restore_command = 'cp ${wal}/%f %p'
recovery_target_time = '${target_time}'
recovery_target_action = 'promote'
EOF
touch "${PGDATA}/recovery.signal"

echo "PITR staged. Start Postgres against ${PGDATA} to begin recovery."
echo "Watch the log for: 'recovery stopping before commit of transaction'"
