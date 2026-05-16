#!/usr/bin/env bash
# Apply all SQL migrations in db/migrations/ to $DATABASE_URL.
# Each migration is idempotent, so re-runs are safe.
#
# Flags:
#   --with-seed   also run db/seed.sh after migrations (dev/CI use; the
#                 seed script itself refuses to run in production)
#
# Reference data (delivery tiers, prohibited items) is part of the
# migrations (0011_init_seed_reference_data.sql) and is always applied.
# Persona/test accounts live in db/seeds/ and are only applied with
# --with-seed.
set -euo pipefail

: "${DATABASE_URL:?DATABASE_URL must be set, e.g. postgres://user:pass@host:5432/db}"

WITH_SEED=0
for arg in "$@"; do
    case "$arg" in
        --with-seed) WITH_SEED=1 ;;
        -h|--help)
            sed -n '1,15p' "$0"
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            exit 64
            ;;
    esac
done

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

if (( WITH_SEED == 1 )); then
    echo
    echo "-- running dev/test seed --"
    "${SCRIPT_DIR}/seed.sh"
fi
