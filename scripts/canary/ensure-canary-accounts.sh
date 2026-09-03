#!/usr/bin/env bash
# Idempotent verification of the two fixed canary identities; creates nothing.
# See docs/runbooks/chat-push-canary.md.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/canary/lib.sh
. "$SCRIPT_DIR/lib.sh"

BASE_URL="${JEEB_CANARY_BASE_URL:-https://app.jeeb.fds-1.com}"
CLIENT_ID="${JEEB_CANARY_CLIENT_ID:-canary-chat-push-client}"
JEEBER_ID="${JEEB_CANARY_JEEBER_ID:-canary-chat-push-jeeber}"
AVAIL_PREFIX="${JEEB_CANARY_AVAILABILITY_PREFIX:-/v1}"
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
check_identity() {
  local user="$1" role="$2" out
  out="$(canary_tmpfile "mint-$role")"
  canary_http POST "$BASE_URL/auth/tokens" \
    --header-var "X-Service-Auth-Key:JEEB_TOKEN_MINT_KEY" \
    --json "$(jq -nc --arg u "$user" --arg r "$role" '{userId: $u, roles: [$r]}')" \
    --no-preview --out "$out"
  canary_expect accounts "200" "$role bearer mint for $user"
  MINTED_TOKEN="$(canary_access_token <"$out")"
  [ "$CANARY_MODE" != execute ] || [ -n "$MINTED_TOKEN" ] || \
    canary_fail accounts "$role mint for $user returned no accessToken"
}

check_identity "$CLIENT_ID" client; CLIENT_TOKEN="$MINTED_TOKEN"; export CLIENT_TOKEN
check_identity "$JEEBER_ID" jeeber; JEEBER_TOKEN="$MINTED_TOKEN"; export JEEBER_TOKEN
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

canary_log ""
canary_log "READY: client=$CLIENT_ID jeeber=$JEEBER_ID on $BASE_URL"
canary_log "Notes: KYC approval is not required for fan-out today. Keep both ids"
canary_log "NON-GUID — a GUID-shaped jeeber id triggers the offer wallet guard."
exit 0
