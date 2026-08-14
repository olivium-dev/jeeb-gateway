#!/usr/bin/env bash
# gwdbx W4-10 — tiers catalog import: gateway tiers -> delivery-service (freeze-import-flip).
# OWNER-RUN (G-21) and O4-GATED: the catalog-id mapping is the O4 business ruling.
# Run only inside the W4-09/A14 authoring freeze (no /admin/tiers edits mid-window).
#
# Usage:
#   GATEWAY_DSN='postgres://...jeeb_gateway' DELIVERY_BASE_URL='http://127.0.0.1:5802' \
#     bash scripts/gwdbx/w4-10-tiers-backfill.sh --o4 keep-gateway-codes [--apply] [--verify-noop]
#
# --o4 names the ruling being executed (recorded into actor_ref):
#     keep-gateway-codes   import urgent/same-day/scheduled as-is (delivery keeps its own too)
#     map:<a=b,c=d,...>    rename gateway codes on import per the owner's explicit mapping
# Default is DRY-RUN. --verify-noop re-imports and asserts everything unchanged.
set -euo pipefail

APPLY=0
VERIFY_NOOP=0
O4=""
while [ $# -gt 0 ]; do
  case "$1" in
    --apply) APPLY=1 ;;
    --verify-noop) VERIFY_NOOP=1 ;;
    --o4) shift; O4="${1:-}" ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
  shift
done

: "${GATEWAY_DSN:?set GATEWAY_DSN}"
: "${DELIVERY_BASE_URL:?set DELIVERY_BASE_URL}"
# O4 is a genuine owner ruling — refuse to run without it being named explicitly.
[ -n "$O4" ] || { echo "FATAL: --o4 <ruling> is required (O4 is owner-gated)" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

psql "$GATEWAY_DSN" -Atc "
  SELECT json_build_object('tiers', COALESCE(json_agg(json_build_object(
    'code', lower(id),
    'display_name_en', name,
    'tagline_en', price_hint,
    'ttl_minutes', GREATEST(1, sla_hours) * 60,
    'radius_km', GREATEST(1, round(radius_km)::int),
    'max_providers', 25,
    'pricing_multiplier', 1.0,
    'commission_rate', commission_rate,
    'price_hint', price_hint,
    'request_ttl_seconds', request_ttl_seconds,
    'sla_hours', sla_hours
  )), '[]'::json))
  FROM tiers" > "$WORK/payload.json"

ROWS=$(python3 -c "import json; print(len(json.load(open('$WORK/payload.json'))['tiers']))")
TOTAL=$(psql "$GATEWAY_DSN" -Atc "SELECT count(*) FROM tiers")
if [ "$TOTAL" -gt 0 ] && [ "$ROWS" -eq 0 ]; then
  echo "FATAL: tiers has $TOTAL rows but the export built 0 — export is broken" >&2
  exit 1
fi

# Apply the O4 mapping if the ruling renames codes.
case "$O4" in
  map:*)
    python3 - "$WORK/payload.json" "${O4#map:}" <<'PY'
import json, sys
path, spec = sys.argv[1], sys.argv[2]
mapping = dict(pair.split("=", 1) for pair in spec.split(","))
doc = json.load(open(path))
for t in doc["tiers"]:
    t["code"] = mapping.get(t["code"], t["code"])
json.dump(doc, open(path, "w"))
PY
    ;;
esac

echo "plan (O4=$O4): importing $ROWS gateway tiers into $DELIVERY_BASE_URL"
python3 -c "import json; [print(' ', t['code'], '<-', t['display_name_en']) for t in json.load(open('$WORK/payload.json'))['tiers']]"

if [ "$APPLY" -ne 1 ]; then
  echo "DRY-RUN (no POST)."
  exit 0
fi

post_import() {
  curl -sS -w '\n%{http_code}' -X POST "$DELIVERY_BASE_URL/api/v1/tiers/import" \
    -H 'Content-Type: application/json' -H "X-Actor-Ref: gwdbx-w4-10-o4-$O4" \
    --data-binary @"$WORK/payload.json"
}

OUT=$(post_import)
CODE=$(echo "$OUT" | tail -1)
BODY=$(echo "$OUT" | sed '$d')
echo "import: HTTP $CODE  $BODY"
[ "$CODE" = "200" ] || { echo "FATAL: import returned $CODE" >&2; exit 1; }

if [ "$VERIFY_NOOP" -eq 1 ]; then
  OUT2=$(post_import)
  CODE2=$(echo "$OUT2" | tail -1)
  BODY2=$(echo "$OUT2" | sed '$d')
  echo "re-run: HTTP $CODE2  $BODY2"
  CH2=$(echo "$BODY2" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['created']+d['replaced'])")
  if [ "$CODE2" != "200" ] || [ "$CH2" != "0" ]; then
    echo "FATAL: double-run was NOT a no-op (created+replaced=$CH2)" >&2
    exit 1
  fi
  echo "double-run no-op PROVEN (created+replaced=0)"
fi
