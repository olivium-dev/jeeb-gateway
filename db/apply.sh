#!/usr/bin/env bash
# Apply all SQL migrations in db/migrations/ to $DATABASE_URL.
# Each migration is idempotent, so re-runs are safe.
set -euo pipefail

: "${DATABASE_URL:?DATABASE_URL must be set, e.g. postgres://user:pass@host:5432/db}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIGRATIONS_DIR="${SCRIPT_DIR}/migrations"

shopt -s nullglob
files=( "${MIGRATIONS_DIR}"/*.sql )
if (( ${#files[@]} == 0 )); then
    echo "No migrations found in ${MIGRATIONS_DIR}" >&2
    exit 0
fi

for file in "${files[@]}"; do
    echo ">> applying $(basename "$file")"
    psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f "$file"
done

echo "All migrations applied."
