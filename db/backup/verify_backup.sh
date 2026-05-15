#!/usr/bin/env bash
# Smoke-test the latest backup by restoring it into a throwaway Postgres
# container and asserting that core tables exist and are non-empty.
#
# Runs in CI weekly (db-backup-verify workflow) and on demand by SREs.
# A failed verification is a P2 because it means dumps are silently corrupt.
#
# Required:
#   docker daemon available
#   BACKUP_FILE — path to a .dump produced by pg_backup.sh (default: latest)

set -euo pipefail

: "${BACKUP_DIR:=/var/backups/jeeb}"
: "${BACKUP_FILE:=${BACKUP_DIR}/latest.dump}"
: "${VERIFY_PG_IMAGE:=postgres:16-alpine}"

if [[ ! -f "${BACKUP_FILE}" ]]; then
    # Resolve symlink before failing — gives a clearer error message.
    real="$(readlink -f "${BACKUP_FILE}" 2>/dev/null || echo "${BACKUP_FILE}")"
    echo "backup file not found: ${real}" >&2
    exit 1
fi

container="jeeb-backup-verify-$$"
pw="verify-$(date +%s)"

cleanup() { docker rm -f "${container}" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo ">> spinning up throwaway ${VERIFY_PG_IMAGE}"
docker run -d --rm \
    --name "${container}" \
    -e POSTGRES_DB=jeeb_verify \
    -e POSTGRES_USER=verify \
    -e POSTGRES_PASSWORD="${pw}" \
    -p 0:5432 \
    "${VERIFY_PG_IMAGE}" >/dev/null

# Wait until pg_isready inside the container reports ready (max 30s).
for _ in $(seq 1 30); do
    if docker exec "${container}" pg_isready -U verify -d jeeb_verify >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

port="$(docker port "${container}" 5432/tcp | head -1 | awk -F: '{print $NF}')"
url="postgres://verify:${pw}@127.0.0.1:${port}/jeeb_verify"

echo ">> restoring $(basename "${BACKUP_FILE}")"
TARGET_DATABASE_URL="${url}" "$(dirname "$0")/pg_restore.sh" --dump "${BACKUP_FILE}"

echo ">> verifying schema"
expected_tables=(users kyc_submissions schema_migrations)
for t in "${expected_tables[@]}"; do
    if ! docker exec "${container}" psql -U verify -d jeeb_verify -tAc \
            "SELECT 1 FROM information_schema.tables WHERE table_name='${t}'" \
            | grep -q '^1$'; then
        echo "missing table after restore: ${t}" >&2
        exit 1
    fi
done

echo "verification OK"
