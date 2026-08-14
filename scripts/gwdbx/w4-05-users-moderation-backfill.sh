#!/usr/bin/env bash
# gwdbx W4-05 — one-shot idempotent moderation backfill: gateway users -> user-management.
# OWNER-RUN (G-21). Carries ONLY id + suspension state — no phone, so O5 is not prejudged.
#
# Usage:
#   GATEWAY_DSN='postgres://user@host/jeeb_gateway' UM_BASE_URL='http://127.0.0.1:10020' \
#     bash scripts/gwdbx/w4-05-users-moderation-backfill.sh [--apply] [--verify-noop]
#
# Default is DRY-RUN: prints the plan and POSTs nothing. --apply POSTs the import.
# --verify-noop re-POSTs the same payload and asserts updated==0 (double-run no-op).
set -euo pipefail

APPLY=0
VERIFY_NOOP=0
for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    --verify-noop) VERIFY_NOOP=1 ;;
    *) echo "unknown arg: $arg" >&2; exit 2 ;;
  esac
done

: "${GATEWAY_DSN:?set GATEWAY_DSN to the jeeb_gateway postgres DSN}"
: "${UM_BASE_URL:?set UM_BASE_URL to the user-management base URL}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Export: EVERY user row's moderation state (suspended AND active), so the
# import converges upstream even if a stale suspension was mirrored earlier.
psql "$GATEWAY_DSN" -v ON_ERROR_STOP=1 -At -F '' -c "
  SELECT json_build_object('items', COALESCE(json_agg(json_build_object(
    'userId', id,
    'isSuspended', is_suspended,
    'reason', suspension_reason,
    'suspendedAt', to_char(suspended_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'),
    'suspendedBy', 'gwdbx-w4-05-backfill'
  )), '[]'::json))
  FROM users" > "$WORK/payload.json"

TOTAL=$(psql "$GATEWAY_DSN" -Atc "SELECT count(*) FROM users")
SUSPENDED=$(psql "$GATEWAY_DSN" -Atc "SELECT count(*) FROM users WHERE is_suspended")
ROWS=$(python3 -c "import json,sys; print(len(json.load(open('$WORK/payload.json'))['items']))")

# Anti-vacuity: an empty export with a non-empty table is a broken export, not
# a clean result (sixth-vacuous-zero lesson).
if [ "$TOTAL" -gt 0 ] && [ "$ROWS" -eq 0 ]; then
  echo "FATAL: users has $TOTAL rows but the export built 0 items — export is broken" >&2
  exit 1
fi
echo "plan: $ROWS rows ($SUSPENDED suspended / $TOTAL total users)"

if [ "$APPLY" -ne 1 ]; then
  echo "DRY-RUN (no POST). First 3 items:"
  python3 -c "import json; print(json.dumps(json.load(open('$WORK/payload.json'))['items'][:3], indent=2))"
  exit 0
fi

post_import() {
  curl -sS -w '\n%{http_code}' -X POST "$UM_BASE_URL/api/User/moderation/import" \
    -H 'Content-Type: application/json' --data-binary @"$WORK/payload.json"
}

OUT=$(post_import)
CODE=$(echo "$OUT" | tail -1)
BODY=$(echo "$OUT" | sed '$d')
echo "import: HTTP $CODE  $BODY"
[ "$CODE" = "200" ] || { echo "FATAL: import returned $CODE" >&2; exit 1; }

MISSING=$(echo "$BODY" | python3 -c "import json,sys; print(len(json.load(sys.stdin)['missing']))")
if [ "$MISSING" -gt 0 ]; then
  echo "WARN: $MISSING gateway user ids are unknown to user-management (never projected)." >&2
  echo "      They are listed in the response above; O5 decides how identity backfills." >&2
fi

if [ "$VERIFY_NOOP" -eq 1 ]; then
  OUT2=$(post_import)
  CODE2=$(echo "$OUT2" | tail -1)
  BODY2=$(echo "$OUT2" | sed '$d')
  UPDATED2=$(echo "$BODY2" | python3 -c "import json,sys; print(json.load(sys.stdin)['updated'])")
  echo "re-run: HTTP $CODE2  $BODY2"
  if [ "$CODE2" != "200" ] || [ "$UPDATED2" != "0" ]; then
    echo "FATAL: double-run was NOT a no-op (updated=$UPDATED2)" >&2
    exit 1
  fi
  echo "double-run no-op PROVEN (updated=0)"
fi
