#!/usr/bin/env bash
# Apply development/test seed data (P1-P5 persona accounts, demo KYC,
# demo saved addresses) to $DATABASE_URL.
#
# Reference data (delivery tiers, prohibited items) is NOT seeded here —
# that lives in db/migrations/0011_init_seed_reference_data.sql and is
# applied by apply.sh because production needs it.
#
# This script refuses to run if it looks like DATABASE_URL is pointing
# at production. The check is intentionally conservative: rename a host
# to "prod-anything" and seed.sh will bail.
set -euo pipefail

: "${DATABASE_URL:?DATABASE_URL must be set, e.g. postgres://user:pass@host:5432/db}"

# Production-host guard. Override with FORCE_SEED=1 if you really know
# what you're doing (e.g. seeding a staging environment that happens to
# carry "prod" in the hostname).
if [[ "${FORCE_SEED:-0}" != "1" ]]; then
    case "$DATABASE_URL" in
        *prod*|*production*|*live*)
            echo "Refusing to seed: DATABASE_URL looks like production ($DATABASE_URL)." >&2
            echo "Set FORCE_SEED=1 to override." >&2
            exit 2
            ;;
    esac
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SEEDS_DIR="${SCRIPT_DIR}/seeds"

shopt -s nullglob
files=( "${SEEDS_DIR}"/*.sql )
if (( ${#files[@]} == 0 )); then
    echo "No seed files found in ${SEEDS_DIR}" >&2
    exit 0
fi

for file in "${files[@]}"; do
    echo ">> seeding $(basename "$file")"
    psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f "$file"
done

echo "All seeds applied."
