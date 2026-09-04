#!/usr/bin/env bash
# Idempotent verification of the two fixed canary identities; creates nothing.
# See docs/runbooks/chat-push-canary.md.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/canary/lib.sh
. "$SCRIPT_DIR/lib.sh"

BASE_URL="${JEEB_CANARY_BASE_URL:-https://app.jeeb.fds-1.com}"
# Fixed UUIDs — ban-service rejects a non-UUID id and [RequireActiveUser] then
# fails closed with 503 on request create, offer submit and accept.
CLIENT_ID="${JEEB_CANARY_CLIENT_ID:-ca9a4100-0000-4000-8000-000000000001}"
JEEBER_ID="${JEEB_CANARY_JEEBER_ID:-ca9a4100-0000-4000-8000-000000000002}"
PARTNER_HOLDER_ID="${JEEB_CANARY_PARTNER_HOLDER_ID:-ca9a4100-0000-4000-8000-000000000003}"
ADMIN_ID="${JEEB_CANARY_ADMIN_ID:-ca9a4100-0000-4000-8000-000000000004}"
AVAIL_PREFIX="${JEEB_CANARY_AVAILABILITY_PREFIX:-/v1}"
WALLET_MIN="${JEEB_CANARY_WALLET_MIN:-0.60}"
# The OPAQUE vocabulary user-management persists and the OTP-verify path mints.
CLIENT_ROLES="${JEEB_CANARY_CLIENT_ROLES:-customer}"
JEEBER_ROLES="${JEEB_CANARY_JEEBER_ROLES:-driver,customer}"
# Must stay under PartnerWallet__OtpStepUpThreshold (50) or the transfer needs a
# step-up code and this stops being a two-call, unattended top-up.
WALLET_TOPUP="${JEEB_CANARY_WALLET_TOPUP:-40}"
# Derived, never invented: the store keys the reservation by holder and rejects
# any identifier that is not devtool-partner-<holderId without dashes>.
PARTNER_IDENTIFIER="${JEEB_CANARY_PARTNER_IDENTIFIER:-$(canary_runtime_partner_identifier "$PARTNER_HOLDER_ID")}"
PARTNER_PASSWORD="${JEEB_CANARY_PARTNER_PASSWORD:-}"
SKIP_FUNDING="${JEEB_CANARY_SKIP_FUNDING:-false}"
CANARY_MODE=execute

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) BASE_URL="$2"; shift 2 ;;
    --plan) CANARY_MODE=plan; shift ;;
    -h|--help) sed -n '2,3p' "$0"; exit 0 ;;
    *) printf 'unknown argument: %s\n' "$1" >&2; exit 64 ;;
  esac
done

export CANARY_MODE
BASE_URL="${BASE_URL%/}"
CANARY_WORKDIR="$(mktemp -d)"
CANARY_EVIDENCE="$CANARY_WORKDIR/evidence.log"
export CANARY_WORKDIR CANARY_EVIDENCE
trap 'rm -rf "$CANARY_WORKDIR"' EXIT

canary_require_tools
[ "$CANARY_MODE" != execute ] || [ -n "${JEEB_TOKEN_MINT_KEY:-}" ] || \
  canary_fail accounts "JEEB_TOKEN_MINT_KEY is unset"

canary_log "== canary account check on $BASE_URL =="

# Sets MINTED_TOKEN rather than echoing, so the plan output is not swallowed.
MINTED_TOKEN=""
# $2 is the CANONICAL role list, comma-separated; the first entry also becomes
# active_role when no user-management profile exists yet.
mint_bearer_for() {
  local user="$1" roles="$2" label="${3:-$2}" out
  out="$(canary_tmpfile "mint-$label")"
  canary_http POST "$BASE_URL/auth/tokens" \
    --header-var "X-Service-Auth-Key:JEEB_TOKEN_MINT_KEY" \
    --json "$(jq -nc --arg u "$user" --arg r "$roles" \
      '{userId: $u, roles: ($r | split(","))}')" \
    --no-preview --out "$out"
  canary_expect accounts "200" "$label bearer mint for $user"
  MINTED_TOKEN="$(canary_access_token <"$out")"
  [ "$CANARY_MODE" != execute ] || [ -n "$MINTED_TOKEN" ] || \
    canary_fail accounts "$label mint for $user returned no accessToken"
  canary_mask "$MINTED_TOKEN"
}

mint_bearer_for "$CLIENT_ID" "$CLIENT_ROLES" client; CLIENT_TOKEN="$MINTED_TOKEN"; export CLIENT_TOKEN
mint_bearer_for "$JEEBER_ID" "$JEEBER_ROLES" jeeber; JEEBER_TOKEN="$MINTED_TOKEN"; export JEEBER_TOKEN
canary_log "  both canary identities mint (values never printed)"

canary_log "  jeeber — availability surface"
canary_http GET "$BASE_URL$AVAIL_PREFIX/jeebers/me/availability" --bearer-var JEEBER_TOKEN
canary_expect accounts "200" "jeeber availability read (adjust JEEB_CANARY_AVAILABILITY_PREFIX on 404)"

canary_log "  jeeber — device-registration surface"
canary_http PUT "$BASE_URL/api/PushNotification/register" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc --arg d "jeeb-canary-device-$JEEBER_ID" \
    '{fcmToken: "jeeb-canary-fcm-token-provisioning", deviceId: $d}')"
canary_expect accounts "200 201" "jeeber device registration"

canary_log "  client — tier catalog"
canary_http GET "$BASE_URL/tiers" --bearer-var CLIENT_TOKEN
canary_expect accounts "200" "tier catalog read"

# --- Wallet funding. A UUID jeeber re-arms the offer-time wallet-sufficiency
# --- guard, so leg 6 needs a balance. This walks the Dev Tool's own route chain.
canary_log ""
canary_log "  jeeber — wallet balance (offer guard needs >= $WALLET_MIN)"
WALLET_FILE="$(canary_tmpfile jeeber-wallet)"
canary_http GET "$BASE_URL/v1/jeeb/wallet" --bearer-var JEEBER_TOKEN --out "$WALLET_FILE"
canary_expect accounts "200" "canary jeeber wallet read"

FUNDED=skipped
if [ "$SKIP_FUNDING" = "true" ]; then
  canary_note "funding disabled by JEEB_CANARY_SKIP_FUNDING"
elif [ "$CANARY_MODE" = "execute" ] && canary_wallet_sufficient "$WALLET_MIN" <"$WALLET_FILE"; then
  # This early return IS the idempotency: re-running never stacks credits.
  canary_note "wallet already clears the guard — nothing to fund"
  FUNDED=already
else
  canary_log ""
  canary_log "  funding — the Dev Tool 'Fund Jeeber wallet' chain, amount $WALLET_TOPUP"
  [ -n "$PARTNER_PASSWORD" ] || PARTNER_PASSWORD="$(canary_partner_password "$PARTNER_IDENTIFIER" "$PARTNER_HOLDER_ID")"

  mint_bearer_for "$ADMIN_ID" admin; ADMIN_TOKEN="$MINTED_TOKEN"; export ADMIN_TOKEN

  # The transfer target must EXIST in wallet-service first. GET /v1/jeeb/wallet
  # projects an empty balance for an unprovisioned holder, so it cannot see this.
  canary_log "  1/6 ensure the canary jeeber has a provisioned wallet holder"
  canary_http PUT "$BASE_URL/dev/wallets/jeeber/$JEEBER_ID/ensure"
  if [ "$CANARY_MODE" = execute ]; then
    case "$CANARY_LAST_CODE" in
      404) canary_fail accounts "PUT /dev/wallets/jeeber/{id}/ensure is 404 — the [DevOnly] seam is disabled on this gateway, so the canary cannot provision its own funding target" ;;
      502) canary_fail accounts "wallet-service could not converge a holder for $JEEBER_ID (502) — the top-up target does not exist and the transfer would 409 'no provisioned wallet'" ;;
    esac
  fi
  canary_expect accounts "200 204" "canary jeeber wallet-holder provisioning"

  canary_log "  2/6 provision the demo partner credential (holder-bound, removed at the end)"
  CANARY_PARTNER_PROVISION_BODY="$(jq -nc --arg i "$PARTNER_IDENTIFIER" --arg h "$PARTNER_HOLDER_ID" \
    --arg p "$PARTNER_PASSWORD" \
    '{identifier: $i, holderId: $h, displayName: "Jeeb canary funding partner", password: $p}')"
  CANARY_PARTNER_LOGIN_BODY="$(jq -nc --arg i "$PARTNER_IDENTIFIER" --arg p "$PARTNER_PASSWORD" \
    '{identifier: $i, password: $p}')"
  export CANARY_PARTNER_PROVISION_BODY CANARY_PARTNER_LOGIN_BODY
  provision_partner_credential() {
    canary_http POST "$BASE_URL/dev/partner/credentials" \
      --json-var CANARY_PARTNER_PROVISION_BODY
  }
  provision_partner_credential
  # A live reservation is tombstoned, not deleted, and holds a different random
  # password, so it cannot be reclaimed — only waited out.
  if [ "$CANARY_MODE" = execute ] && [ "$CANARY_LAST_CODE" = "409" ]; then
    canary_fail accounts "a runtime partner reservation for $PARTNER_HOLDER_ID is still live — retry after its 5-minute lifetime, or point JEEB_CANARY_PARTNER_HOLDER_ID at a fresh UUID"
  fi
  canary_expect accounts "200 201 204" "partner credential provisioning"

  canary_log "  3/6 sign the partner in"
  LOGIN_FILE="$(canary_tmpfile partner-login)"
  canary_http POST "$BASE_URL/v1/partner/auth/login" \
    --json-var CANARY_PARTNER_LOGIN_BODY \
    --no-preview --out "$LOGIN_FILE"
  canary_expect accounts "200" "partner login"
  PARTNER_TOKEN="$(jq -r '(.accessToken // .token // "")' <"$LOGIN_FILE" 2>/dev/null)"
  PARTNER_ID="$(jq -r '(.partner.partnerId // .partnerId // "")' <"$LOGIN_FILE" 2>/dev/null)"
  export PARTNER_TOKEN
  if [ "$CANARY_MODE" = execute ]; then
    [ -n "$PARTNER_TOKEN" ] || canary_fail accounts "partner login returned no accessToken"
    [ -n "$PARTNER_ID" ] || canary_fail accounts "partner login returned no partnerId"
    canary_mask "$PARTNER_TOKEN"
  else
    PARTNER_ID='<partner_id>'
  fi

  canary_log "  4/6 cash-credit the partner as admin (fixed idempotency key)"
  canary_http POST "$BASE_URL/v1/admin/partners/$PARTNER_ID/wallet/credits" \
    --bearer-var ADMIN_TOKEN \
    --json "$(jq -nc --argjson a "$WALLET_TOPUP" --arg k "jeeb-canary-partner-credit-$PARTNER_HOLDER_ID" \
      '{amount: $a, evidenceNote: "Jeeb chat+push canary funding", idempotencyKey: $k}')"
  canary_expect accounts "200 201 409" "partner cash credit"

  canary_log "  5/6 preview the top-up — it must NOT require a step-up code"
  PREVIEW_FILE="$(canary_tmpfile topup-preview)"
  canary_http POST "$BASE_URL/v1/partner/wallet/transfers/predict" \
    --bearer-var PARTNER_TOKEN \
    --json "$(jq -nc --arg j "$JEEBER_ID" --argjson a "$WALLET_TOPUP" '{jeeberId: $j, amount: $a}')" \
    --out "$PREVIEW_FILE"
  canary_expect accounts "200" "top-up preview"
  if [ "$CANARY_MODE" = execute ] && jq -e '.otpRequired == true' <"$PREVIEW_FILE" >/dev/null 2>&1; then
    canary_fail accounts "the top-up of $WALLET_TOPUP is above PartnerWallet__OtpStepUpThreshold and would need a step-up code — lower JEEB_CANARY_WALLET_TOPUP"
  fi

  canary_log "  6/6 transfer to the canary jeeber (fixed idempotency key)"
  canary_http POST "$BASE_URL/v1/partner/wallet/transfers" \
    --bearer-var PARTNER_TOKEN \
    --json "$(jq -nc --arg j "$JEEBER_ID" --argjson a "$WALLET_TOPUP" \
      --arg k "jeeb-canary-topup-$JEEBER_ID" \
      '{jeeberId: $j, amount: $a, idempotencyKey: $k, note: "Jeeb chat+push canary funding"}')"
  canary_expect accounts "200 201 409" "partner to jeeber transfer"

  canary_log "  cleanup — remove the temporary partner credential"
  canary_http DELETE "$BASE_URL/dev/partner/credentials/$PARTNER_IDENTIFIER?holderId=$PARTNER_HOLDER_ID"
  FUNDED=funded

  canary_log "  re-read the balance"
  canary_http GET "$BASE_URL/v1/jeeb/wallet" --bearer-var JEEBER_TOKEN --out "$WALLET_FILE"
  canary_expect accounts "200" "canary jeeber wallet re-read"
  [ "$CANARY_MODE" != execute ] || canary_wallet_sufficient "$WALLET_MIN" <"$WALLET_FILE" || \
    canary_fail accounts "the wallet is still below $WALLET_MIN after funding — the transfer did not land"
  canary_note "wallet now clears the offer guard"
fi

canary_log ""
canary_log "READY: client=$CLIENT_ID jeeber=$JEEBER_ID on $BASE_URL (funding: $FUNDED)"
canary_log "Notes: both ids MUST stay well-formed UUIDs — ban-service rejects anything"
canary_log "else and [RequireActiveUser] then 503s create, offer submit and accept."
exit 0
