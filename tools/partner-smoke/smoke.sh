#!/usr/bin/env bash
#
# partner-smoke/smoke.sh — API-contract smoke pack for the Partner Portal wallet BFF
# (branch feat/partner-wallet-bff). Covers every partner/admin endpoint plus the
# auth-missing (401), wrong-role (403), and bad-input (400) negative cases, and an
# idempotency-replay money-safety assertion.
#
# ─────────────────────────────────────────────────────────────────────────────────
# THIS SCRIPT DOES NOT RUN AGAINST ANY HOST UNTIL YOU EXPLICITLY POINT IT AT ONE.
# It fail-fasts unless BASE_URL is set. It targets a DEV/STAGING gateway only
# (MSI :10090); NEVER a production money host. It moves REAL money on a live wallet
# host — run it only against a throwaway dev partner/jeeber, never in prod.
# ─────────────────────────────────────────────────────────────────────────────────
#
# Endpoints under test (actual route contract on feat/partner-wallet-bff):
#   GET  v1/partner/wallet                         cap partner.wallet.read.own {partner}
#   GET  v1/partner/wallet/ledger                  cap partner.wallet.read.own {partner}
#   POST v1/partner/wallet/transfers/predict       cap partner.topup.execute  {partner}
#   POST v1/partner/wallet/transfers               cap partner.topup.execute  {partner}
#   GET  v1/partner/jeebers/{id}/wallet-target     cap partner.jeeber.lookup  {partner}
#   POST v1/admin/partners/{id}/wallet/credits     cap partner.wallet.credit  {admin}
#
# NOTE: the balance route is GET v1/partner/wallet (NO /balance suffix). The build
# report §3.1 documents it as ".../wallet/balance" — that path 404s. This pack asserts
# the real route AND probes the documented one so the drift is visible.
#
# Auth model: the gateway trusts the edge identity headers X-User-Id / X-User-Roles
# ONLY from a trusted edge or a Development/Testing host (UserIdentity.cs / SEC-C1). On
# such a host, set AUTH_MODE=edge (default). Against a JWT-enforcing host, set
# AUTH_MODE=bearer and supply PARTNER_TOKEN / ADMIN_TOKEN.
#
# Usage:
#   BASE_URL=http://192.168.2.39:10090 \
#   PARTNER_ID=<partner-guid> JEEBER_ID=<jeeber-guid> \
#   ./smoke.sh
#
#   # JWT host:
#   BASE_URL=https://gw.example \
#   AUTH_MODE=bearer PARTNER_TOKEN=... ADMIN_TOKEN=... \
#   PARTNER_ID=<guid> JEEBER_ID=<guid> ./smoke.sh
#
# Env:
#   BASE_URL       (required) gateway base, no trailing slash
#   AUTH_MODE      edge (default) | bearer
#   PARTNER_ID     partner holder GUID (edge mode: sent as X-User-Id)
#   JEEBER_ID      jeeber holder GUID (top-up destination)
#   ADMIN_ID       admin operator GUID (edge mode; default random)
#   PARTNER_TOKEN  bearer for partner calls (bearer mode)
#   ADMIN_TOKEN    bearer for admin calls (bearer mode)
#   AMOUNT         top-up/credit amount (default 5)
#   MOVE_MONEY     unset/0 = skip the money-MOVING calls (predict is safe, execute/credit
#                  are gated); set MOVE_MONEY=1 to actually exercise execute + credit.
#
set -uo pipefail

# ── config / guards ────────────────────────────────────────────────────────────────
BASE_URL="${BASE_URL:-}"
if [[ -z "$BASE_URL" ]]; then
  echo "REFUSING TO RUN: BASE_URL is unset. This pack does not target any host by default." >&2
  echo "Set BASE_URL to a DEV/STAGING gateway (e.g. http://192.168.2.39:10090) and re-run." >&2
  exit 2
fi
BASE_URL="${BASE_URL%/}"

AUTH_MODE="${AUTH_MODE:-edge}"
PARTNER_ID="${PARTNER_ID:-11111111-1111-1111-1111-111111111111}"
JEEBER_ID="${JEEBER_ID:-22222222-2222-2222-2222-222222222222}"
ADMIN_ID="${ADMIN_ID:-33333333-3333-3333-3333-333333333333}"
AMOUNT="${AMOUNT:-5}"
MOVE_MONEY="${MOVE_MONEY:-0}"

# Hardened curl defaults (curl-api-testing skill). --max-time caps hung connections.
CURL=(curl -sS --max-time 30 -H 'Accept: application/json' -H 'Content-Type: application/json')

pass=0; fail=0; warn=0

# role-scoped auth header args
auth_partner() {
  if [[ "$AUTH_MODE" == "bearer" ]]; then printf '%s\0%s\0' "-H" "Authorization: Bearer ${PARTNER_TOKEN:-}";
  else printf '%s\0%s\0%s\0%s\0' "-H" "X-User-Id: $PARTNER_ID" "-H" "X-User-Roles: partner"; fi
}
auth_admin() {
  if [[ "$AUTH_MODE" == "bearer" ]]; then printf '%s\0%s\0' "-H" "Authorization: Bearer ${ADMIN_TOKEN:-}";
  else printf '%s\0%s\0%s\0%s\0' "-H" "X-User-Id: $ADMIN_ID" "-H" "X-User-Roles: admin"; fi
}
auth_jeeber() {  # a valid non-partner identity, for wrong-role 403 probes
  if [[ "$AUTH_MODE" == "bearer" ]]; then printf '%s\0%s\0' "-H" "Authorization: Bearer ${JEEBER_TOKEN:-}";
  else printf '%s\0%s\0%s\0%s\0' "-H" "X-User-Id: $JEEBER_ID" "-H" "X-User-Roles: driver"; fi
}

# read a NUL-delimited header list into an array
read_headers() { local -n _out=$1; shift; mapfile -d '' -t _out < <("$@"); }

# do <name> <expected-status> <method> <path> [--auth partner|admin|jeeber|none] [--body JSON]
# Prints PASS/FAIL and returns the response body on fd 3 for follow-up assertions.
RESP_BODY=""
do_case() {
  local name="$1" expect="$2" method="$3" path="$4"; shift 4
  local who="none" body=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --auth) who="$2"; shift 2;;
      --body) body="$2"; shift 2;;
      *) shift;;
    esac
  done

  local hdrs=()
  case "$who" in
    partner) read_headers hdrs auth_partner;;
    admin)   read_headers hdrs auth_admin;;
    jeeber)  read_headers hdrs auth_jeeber;;
    none)    hdrs=();;
  esac

  local args=("${CURL[@]}" -X "$method" "${hdrs[@]}" -o /tmp/partner_smoke_body.$$ -w '%{http_code}')
  [[ -n "$body" ]] && args+=(--data "$body")
  args+=("$BASE_URL/$path")

  local code; code="$("${args[@]}")"
  RESP_BODY="$(cat /tmp/partner_smoke_body.$$ 2>/dev/null || true)"; rm -f /tmp/partner_smoke_body.$$

  if [[ "$code" == "$expect" ]]; then
    printf '  PASS  %-46s %s (%s)\n' "$name" "$method /$path" "$code"; pass=$((pass+1))
  else
    printf '  FAIL  %-46s %s expected %s got %s\n' "$name" "$method /$path" "$expect" "$code"; fail=$((fail+1))
    [[ -n "$RESP_BODY" ]] && echo "        body: $(echo "$RESP_BODY" | head -c 300)"
  fi
}

# assert the body is RFC 7807 problem+json (has type+title+status)
assert_problem_shape() {
  local label="$1"
  if echo "$RESP_BODY" | jq -e 'has("title") and has("status")' >/dev/null 2>&1; then
    printf '  PASS  %-46s RFC7807 shape\n' "$label"; pass=$((pass+1))
  else
    printf '  WARN  %-46s non-ProblemDetails error body\n' "$label"; warn=$((warn+1))
  fi
}

echo "== Partner Portal wallet BFF smoke =="
echo "   BASE_URL=$BASE_URL  AUTH_MODE=$AUTH_MODE  MOVE_MONEY=$MOVE_MONEY"
echo "   partner=$PARTNER_ID  jeeber=$JEEBER_ID  amount=$AMOUNT"
echo

# ── 1. AUTH-MISSING → 401 (every authenticated route) ───────────────────────────────
echo "-- auth-missing (expect 401) --"
do_case "balance no-auth"        401 GET  "v1/partner/wallet"                       --auth none
do_case "ledger no-auth"         401 GET  "v1/partner/wallet/ledger"               --auth none
do_case "predict no-auth"        401 POST "v1/partner/wallet/transfers/predict"    --auth none --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT}"
do_case "transfer no-auth"       401 POST "v1/partner/wallet/transfers"            --auth none --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"idem-noauth-0001\"}"
do_case "jeeber-target no-auth"  401 GET  "v1/partner/jeebers/$JEEBER_ID/wallet-target" --auth none
do_case "admin-credit no-auth"   401 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth none --body "{\"amount\":$AMOUNT,\"evidenceNote\":\"noauth\"}"
echo

# ── 2. WRONG-ROLE → 403 (partner routes hit by a jeeber; admin route hit by a partner) ─
echo "-- wrong-role (expect 403) --"
do_case "balance as-jeeber"      403 GET  "v1/partner/wallet"                       --auth jeeber
do_case "ledger as-jeeber"       403 GET  "v1/partner/wallet/ledger"               --auth jeeber
do_case "predict as-jeeber"      403 POST "v1/partner/wallet/transfers/predict"    --auth jeeber --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT}"
do_case "transfer as-jeeber"     403 POST "v1/partner/wallet/transfers"            --auth jeeber --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"idem-wrongrole-01\"}"
do_case "jeeber-target as-jeeber" 403 GET "v1/partner/jeebers/$JEEBER_ID/wallet-target" --auth jeeber
# admin cash-credit must reject a NON-admin (partner) caller — BFLA guard
do_case "admin-credit as-partner" 403 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth partner --body "{\"amount\":$AMOUNT,\"evidenceNote\":\"partner-cannot-credit\"}"
assert_problem_shape "403 problem+json"
echo

# ── 3. BAD INPUT → 400 (DataAnnotations → ValidationProblemDetails) ─────────────────
echo "-- bad input (expect 400) --"
do_case "predict zero-amount"    400 POST "v1/partner/wallet/transfers/predict"    --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":0}"
do_case "transfer zero-amount"   400 POST "v1/partner/wallet/transfers"            --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":0,\"idempotencyKey\":\"idem-zero-000001\"}"
do_case "transfer no-idem-key"   400 POST "v1/partner/wallet/transfers"            --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT}"
do_case "transfer short-idem"    400 POST "v1/partner/wallet/transfers"            --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"short\"}"
do_case "transfer over-max"      400 POST "v1/partner/wallet/transfers"            --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":100000.01,\"idempotencyKey\":\"idem-overmax-0001\"}"
do_case "admin-credit no-note"   400 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth admin --body "{\"amount\":$AMOUNT}"
do_case "admin-credit short-note" 400 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth admin --body "{\"amount\":$AMOUNT,\"evidenceNote\":\"no\"}"
assert_problem_shape "400 problem+json"
echo

# ── 4. CONTRACT-DRIFT probes (documented-but-wrong routes SHOULD 404) ───────────────
echo "-- contract-drift probes --"
# Build report §3.1 documents GET .../wallet/balance; the real route has no /balance.
do_case "documented /wallet/balance 404s" 404 GET "v1/partner/wallet/balance"      --auth partner
# Portal (G1) built POST .../topups; the gateway route is .../transfers.
do_case "portal-assumed /topups 404s"     404 POST "v1/partner/topups"             --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"idem-drift-00001\"}"
echo

# ── 5. HAPPY-PATH reads (safe; no money moves) ──────────────────────────────────────
echo "-- happy-path reads --"
do_case "balance ok"             200 GET  "v1/partner/wallet"                       --auth partner
do_case "ledger ok"             200 GET  "v1/partner/wallet/ledger?page=1&pageSize=20" --auth partner
do_case "jeeber-target ok"      200 GET  "v1/partner/jeebers/$JEEBER_ID/wallet-target"  --auth partner
do_case "predict ok (no move)"  200 POST "v1/partner/wallet/transfers/predict"     --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT}"
echo

# ── 6. MONEY-MOVING + idempotency-replay (gated behind MOVE_MONEY=1) ────────────────
echo "-- money-moving + idempotency (gated MOVE_MONEY=$MOVE_MONEY) --"
if [[ "$MOVE_MONEY" == "1" ]]; then
  IDEM="idem-replay-$(date +%s)"
  do_case "transfer execute #1"  200 POST "v1/partner/wallet/transfers"            --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"$IDEM\"}"
  tx1="$(echo "$RESP_BODY" | jq -r '.transactionId // empty' 2>/dev/null)"
  do_case "transfer execute #2 (same idem key)" 200 POST "v1/partner/wallet/transfers" --auth partner --body "{\"jeeberId\":\"$JEEBER_ID\",\"amount\":$AMOUNT,\"idempotencyKey\":\"$IDEM\"}"
  tx2="$(echo "$RESP_BODY" | jq -r '.transactionId // empty' 2>/dev/null)"
  # MONEY-SAFETY ASSERTION: a replayed confirm MUST return the same transactionId
  # (dedup), not create a second money move. On feat/partner-wallet-bff this is
  # EXPECTED TO FAIL — the idempotency key is only written to Notes, never deduped,
  # so #2 mints a second transaction = double-spend. A failing line here is the QA
  # signal for the P0 idempotency defect, not a script bug.
  if [[ -n "$tx1" && "$tx1" == "$tx2" ]]; then
    printf '  PASS  %-46s idempotent replay (tx1==tx2=%s)\n' "idempotency dedup" "$tx1"; pass=$((pass+1))
  else
    printf '  FAIL  %-46s DOUBLE-SPEND: tx1=%s tx2=%s (idem key not deduped)\n' "idempotency dedup" "${tx1:-none}" "${tx2:-none}"; fail=$((fail+1))
  fi

  # Admin cash-credit has NO idempotency field at all — document the exposure: two
  # identical credits create money twice with no dedup key to reconcile them.
  do_case "admin-credit #1"      200 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth admin --body "{\"amount\":$AMOUNT,\"evidenceNote\":\"smoke handover A\"}"
  do_case "admin-credit #2 (dup)" 200 POST "v1/admin/partners/$PARTNER_ID/wallet/credits" --auth admin --body "{\"amount\":$AMOUNT,\"evidenceNote\":\"smoke handover A\"}"
  echo "  NOTE  admin cash-credit exposes no idempotency key; the two credits above BOTH minted money."
else
  echo "  SKIP  execute + admin-credit (set MOVE_MONEY=1 on a throwaway dev holder to exercise)"
fi
echo

# ── summary ─────────────────────────────────────────────────────────────────────────
echo "== summary: $pass passed, $fail failed, $warn warn =="
[[ "$fail" -eq 0 ]] || exit 1
