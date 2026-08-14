#!/usr/bin/env bash
# gwdbx W4 — tiers parity comparer, COMPARE-ONLY (writes nothing anywhere).
# Measures the drift between the gateway tiers catalog and delivery-service's
# serving catalog, so the O4 ruling lands on numbers instead of guesses.
#
# Usage:
#   GATEWAY_DSN='postgres://...jeeb_gateway' DELIVERY_BASE_URL='http://127.0.0.1:5802' \
#     bash scripts/gwdbx/w4-tiers-parity.sh
set -euo pipefail

: "${GATEWAY_DSN:?set GATEWAY_DSN}"
: "${DELIVERY_BASE_URL:?set DELIVERY_BASE_URL}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

psql "$GATEWAY_DSN" -Atc "
  SELECT lower(id) || '|' || name || '|' || sla_hours || '|' || request_ttl_seconds
         || '|' || commission_rate
  FROM tiers ORDER BY id" > "$WORK/gw.tiers"

curl -sSf "$DELIVERY_BASE_URL/api/v1/tiers" > "$WORK/up.json"
python3 - "$WORK/up.json" <<'PY' > "$WORK/up.tiers"
import json, sys
for t in sorted(json.load(open(sys.argv[1])), key=lambda t: t["code"]):
    rts = t.get("request_ttl_seconds") or t.get("ttl_seconds") or 0
    print(f'{t["code"]}|{t.get("name","")}|{t.get("slaHours","")}|{rts}|{t.get("commissionRate","")}')
PY

GW_N=$(wc -l < "$WORK/gw.tiers" | tr -d ' ')
UP_N=$(wc -l < "$WORK/up.tiers" | tr -d ' ')
# Anti-vacuity: an empty side means a broken probe, not parity.
if [ "$GW_N" -eq 0 ] || [ "$UP_N" -eq 0 ]; then
  echo "FATAL: vacuous comparison (gateway=$GW_N, delivery=$UP_N tiers)" >&2
  exit 1
fi

cut -d'|' -f1 "$WORK/gw.tiers" | sort > "$WORK/gw.codes"
cut -d'|' -f1 "$WORK/up.tiers" | sort > "$WORK/up.codes"

echo "=== gwdbx W4 tiers parity ($(date -u '+%Y-%m-%dT%H:%M:%SZ')) ==="
echo "gateway tiers ($GW_N):"
sed 's/^/  /' "$WORK/gw.tiers"
echo "delivery serving catalog ($UP_N):"
sed 's/^/  /' "$WORK/up.tiers"
echo "codes only in gateway:  $(comm -23 "$WORK/gw.codes" "$WORK/up.codes" | tr '\n' ' ')"
echo "codes only in delivery: $(comm -13 "$WORK/gw.codes" "$WORK/up.codes" | tr '\n' ' ')"
SHARED=$(comm -12 "$WORK/gw.codes" "$WORK/up.codes" | wc -l | tr -d ' ')
echo "shared codes:           $SHARED"
if [ "$SHARED" -gt 0 ]; then
  echo "field drift on shared codes (gateway vs delivery lines above differ):"
  join -t'|' -j1 <(sort "$WORK/gw.tiers") <(sort "$WORK/up.tiers") \
    | awk -F'|' '{ if ($2!=$6 || $3!=$7 || $4!=$8 || $5!=$9)
        printf "  %s: name %s/%s sla %s/%s rts %s/%s comm %s/%s\n",$1,$2,$6,$3,$7,$4,$8,$5,$9 }'
fi
