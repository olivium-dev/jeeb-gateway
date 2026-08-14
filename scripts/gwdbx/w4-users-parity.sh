#!/usr/bin/env bash
# gwdbx W4 — users parity comparer, COMPARE-ONLY (writes nothing anywhere).
# Measures the drift between the gateway users projection and user-management,
# so the O5 ruling lands on numbers instead of guesses.
#
# Usage:
#   GATEWAY_DSN='postgres://...jeeb_gateway' UM_DSN='postgres://...user-mgmt-db' \
#     bash scripts/gwdbx/w4-users-parity.sh
#
# No PII is printed: ids and counts only.
set -euo pipefail

: "${GATEWAY_DSN:?set GATEWAY_DSN}"
: "${UM_DSN:?set UM_DSN}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

psql "$GATEWAY_DSN" -Atc "SELECT id FROM users ORDER BY id" > "$WORK/gw.ids"
psql "$UM_DSN" -Atc 'SELECT "Id" FROM "Users" ORDER BY "Id"' > "$WORK/um.ids"

GW_N=$(wc -l < "$WORK/gw.ids" | tr -d ' ')
UM_N=$(wc -l < "$WORK/um.ids" | tr -d ' ')

# Anti-vacuity control: both sides must be non-empty for any "0 drift" to mean
# anything — an unreachable DB reads as empty, and empty==empty is a lie.
if [ "$GW_N" -eq 0 ] || [ "$UM_N" -eq 0 ]; then
  echo "FATAL: vacuous comparison (gateway=$GW_N, um=$UM_N rows) — check DSNs" >&2
  exit 1
fi

GW_ONLY=$(comm -23 "$WORK/gw.ids" "$WORK/um.ids" | wc -l | tr -d ' ')
UM_ONLY=$(comm -13 "$WORK/gw.ids" "$WORK/um.ids" | wc -l | tr -d ' ')
SHARED=$(comm -12 "$WORK/gw.ids" "$WORK/um.ids" | wc -l | tr -d ' ')

GW_SUSP=$(psql "$GATEWAY_DSN" -Atc "SELECT count(*) FROM users WHERE is_suspended")
GW_PHONE=$(psql "$GATEWAY_DSN" -Atc "SELECT count(*) FROM users WHERE phone IS NOT NULL AND btrim(phone) <> ''")
UM_HASH=$(psql "$UM_DSN" -Atc 'SELECT count(*) FROM "Users" WHERE "PhoneHash" IS NOT NULL')

# The W4-03 moderation columns may not exist upstream yet; report that as a
# named drift class instead of failing.
if psql "$UM_DSN" -Atc "SELECT 1 FROM information_schema.columns
      WHERE table_name = 'Users' AND column_name = 'IsSuspended'" | grep -q 1; then
  UM_SUSP=$(psql "$UM_DSN" -Atc 'SELECT count(*) FROM "Users" WHERE "IsSuspended"')
  MODERATION_COLS=present
else
  UM_SUSP="n/a"
  MODERATION_COLS="ABSENT (W4-03 migration not applied upstream)"
fi

echo "=== gwdbx W4 users parity ($(date -u '+%Y-%m-%dT%H:%M:%SZ')) ==="
echo "gateway.users rows:            $GW_N"
echo "user-management Users rows:    $UM_N"
echo "shared ids:                    $SHARED"
echo "gateway-only ids (drift):      $GW_ONLY"
echo "um-only ids (informational):   $UM_ONLY"
echo "suspended: gateway=$GW_SUSP  um=$UM_SUSP  (moderation cols: $MODERATION_COLS)"
echo "phone: gateway raw-phone rows=$GW_PHONE  um phone-hash rows=$UM_HASH  (O5 scope)"
if [ "$GW_ONLY" -gt 0 ]; then
  echo "--- first 10 gateway-only ids ---"
  comm -23 "$WORK/gw.ids" "$WORK/um.ids" | head -10
fi
