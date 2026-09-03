#!/usr/bin/env bash
# End-to-end chat + push OUTCOME canary. See docs/runbooks/chat-push-canary.md.
# Usage: run.sh --base-url https://app.jeeb.fds-1.com [--plan|--execute]

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/canary/lib.sh
. "$SCRIPT_DIR/lib.sh"

BASE_URL="${JEEB_CANARY_BASE_URL:-https://app.jeeb.fds-1.com}"
TIMEOUT="${JEEB_CANARY_TIMEOUT:-150}"
PUSH_BUDGET="${JEEB_CANARY_PUSH_BUDGET:-60}"
CHAT_BUDGET="${JEEB_CANARY_CHAT_BUDGET:-30}"
FIRESTORE_BUDGET="${JEEB_CANARY_FIRESTORE_BUDGET:-30}"
CLIENT_ID="${JEEB_CANARY_CLIENT_ID:-canary-chat-push-client}"
JEEBER_ID="${JEEB_CANARY_JEEBER_ID:-canary-chat-push-jeeber}"
# HARD RULE: offshore, so no real tester is ever inside the 3 km Flash fan-out
# radius. Only the canary jeeber's own uploaded fix satisfies it.
LAT="${JEEB_CANARY_LAT:-33.9500}"
LNG="${JEEB_CANARY_LNG:-35.2000}"
DROP_LAT="${JEEB_CANARY_DROP_LAT:-33.9600}"
DROP_LNG="${JEEB_CANARY_DROP_LNG:-35.2100}"
AVAIL_PREFIX="${JEEB_CANARY_AVAILABILITY_PREFIX:-/v1}"
PUSH_LEDGER_BASE_URL="${PUSH_LEDGER_BASE_URL:-}"
PUSH_CALLER_ID="${JEEB_PUSH_CALLER_ID:-jeeb-gateway}"
FIREBASE_PROJECT_ID="${JEEB_FIREBASE_PROJECT_ID:-jeeb-5a293}"
FIREBASE_DATABASE_ID="${JEEB_FIREBASE_DATABASE_ID:-(default)}"
ALLOW_FCM_TOKEN_REJECT="${JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT:-true}"
ACCEPT_OFFER="${JEEB_CANARY_ACCEPT_OFFER:-true}"
CANARY_MODE=plan

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) BASE_URL="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --client-id) CLIENT_ID="$2"; shift 2 ;;
    --jeeber-id) JEEBER_ID="$2"; shift 2 ;;
    --plan) CANARY_MODE=plan; shift ;;
    --execute) CANARY_MODE=execute; shift ;;
    -h|--help) sed -n '2,3p' "$0"; exit 0 ;;
    *) printf 'unknown argument: %s\n' "$1" >&2; exit 64 ;;
  esac
done

export CANARY_MODE
BASE_URL="${BASE_URL%/}"
case "$BASE_URL" in
  https://*) ;;
  http://127.0.0.1*|http://localhost*) ;;
  *) printf 'refusing a non-TLS, non-loopback base URL: %s\n' "$BASE_URL" >&2; exit 64 ;;
esac

CANARY_WORKDIR="$(mktemp -d)"
CANARY_EVIDENCE="$CANARY_WORKDIR/evidence.log"
export CANARY_WORKDIR CANARY_EVIDENCE

RUN_TAG="${GITHUB_RUN_ID:-local-$(date +%s)}"
CANARY_HARD_DEADLINE="$(( $(date +%s) + TIMEOUT ))"
export CANARY_HARD_DEADLINE
PUSH_PROOF="none"
REQUEST_ID=""
CLIENT_TOKEN=""
JEEBER_TOKEN=""

canary_cancel_request() {
  local code
  code="$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X DELETE "$BASE_URL$1" \
    -H "Authorization: Bearer $CLIENT_TOKEN" 2>/dev/null)"
  [ "${#code}" -eq 3 ] || code="000"
  printf '%s' "$code"
}

# Runs on EVERY exit, including canary_fail. Without it a failed run leaves the
# request broadcasting for the whole tier TTL and the jeeber online.
canary_cleanup() {
  local status=$?
  if [ "$CANARY_MODE" = "execute" ]; then
    if [ -n "$JEEBER_TOKEN" ]; then
      local offline
      offline="$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X PUT \
        "$BASE_URL$AVAIL_PREFIX/jeebers/me/availability" \
        -H "Authorization: Bearer $JEEBER_TOKEN" -H 'Content-Type: application/json' \
        --data-binary '{"online": false}' 2>/dev/null)"
      [ "${#offline}" -eq 3 ] || offline="000"
      case "$offline" in
        200|204) printf 'cleanup: canary jeeber is offline (HTTP %s)\n' "$offline" ;;
        *) printf '::warning::cleanup: go-offline returned HTTP %s — the canary jeeber may still be online\n' "$offline" ;;
      esac
    fi
    if [ -n "$REQUEST_ID" ] && [ -n "$CLIENT_TOKEN" ]; then
      local code
      # /v1 cancels the delivery too (needed once the offer is accepted); the
      # legacy route is the fallback when no durable delivery row exists.
      code="$(canary_cancel_request "/v1/requests/$REQUEST_ID")"
      [ "$code" = "404" ] && code="$(canary_cancel_request "/requests/$REQUEST_ID")"
      case "$code" in
        200|204|409) printf 'cleanup: request %s closed out (HTTP %s)\n' "$REQUEST_ID" "$code" ;;
        404) printf '::warning::cleanup: request %s not found on either cancel route — it may be orphaned\n' "$REQUEST_ID" ;;
        *) printf '::warning::cleanup: cancel of %s returned HTTP %s — request may be left open\n' "$REQUEST_ID" "$code" ;;
      esac
    fi
  fi
  rm -rf "$CANARY_WORKDIR"
  return $status
}
trap canary_cleanup EXIT

canary_require_tools

canary_log "== jeeb chat+push canary =="
canary_log "  mode        : $CANARY_MODE"
canary_log "  base url    : $BASE_URL"
canary_log "  client id   : $CLIENT_ID"
canary_log "  jeeber id   : $JEEBER_ID"
canary_log "  pickup      : $LAT,$LNG (fixed OFFSHORE coordinate — no real tester in radius)"
canary_log "  budgets     : push ${PUSH_BUDGET}s, chat ${CHAT_BUDGET}s, firestore ${FIRESTORE_BUDGET}s, hard cap ${TIMEOUT}s"
canary_log "  accept leg  : $ACCEPT_OFFER"
canary_log "  push ledger : ${PUSH_LEDGER_BASE_URL:-<unset — falling back to the durable notification inbox>}"
if [ -n "${JEEB_FIREBASE_WEB_API_KEY:-}" ]; then
  canary_log "  firestore   : enabled (web API key present)"
else
  canary_log "  firestore   : disabled (JEEB_FIREBASE_WEB_API_KEY unset)"
fi

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[0/9] preflight"
if [ "$CANARY_MODE" = "execute" ] && [ -z "${JEEB_TOKEN_MINT_KEY:-}" ]; then
  canary_fail preflight "JEEB_TOKEN_MINT_KEY is unset — the canary mints its own bearers and will not fall back to SuperLogin open mode"
fi
canary_http GET "$BASE_URL/health/live"
canary_expect preflight "200" "gateway liveness"

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[1/9] identity — mint the two fixed canary bearers"
canary_log "        X-Service-Auth-Key only; open mode is never relied on."
canary_log "        The ids are deliberately NOT GUIDs, which keeps the offer-time"
canary_log "        wallet-sufficiency guard out of the canary's way."
# Sets MINTED_TOKEN rather than echoing: a command substitution would swallow
# the plan output instead of printing it.
MINTED_TOKEN=""
mint_bearer() {
  local user="$1" role="$2" out
  out="$(canary_tmpfile "mint-$role")"
  canary_http POST "$BASE_URL/auth/tokens" \
    --header-var "X-Service-Auth-Key:JEEB_TOKEN_MINT_KEY" \
    --json "$(jq -nc --arg u "$user" --arg r "$role" '{userId: $u, roles: [$r]}')" \
    --no-preview --out "$out"
  canary_expect identity "200" "$role bearer mint"
  MINTED_TOKEN="$(canary_access_token <"$out")"
  [ "$CANARY_MODE" != execute ] || [ -n "$MINTED_TOKEN" ] || \
    canary_fail identity "$role mint returned no accessToken"
  canary_mask "$MINTED_TOKEN"
}
mint_bearer "$CLIENT_ID" client; CLIENT_TOKEN="$MINTED_TOKEN"
mint_bearer "$JEEBER_ID" jeeber; JEEBER_TOKEN="$MINTED_TOKEN"

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[2/9] presence — availability ON + a GPS fix at the pickup coordinate"
canary_log "        Fan-out AND the offer route are geo-filtered fail-closed, so"
canary_log "        without this the canary would pass vacuously."
canary_http PUT "$BASE_URL$AVAIL_PREFIX/jeebers/me/availability" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc '{online: true, vehicleType: "car", zone: "beirut-central"}')"
canary_expect presence "200" "jeeber go-online"

canary_http POST "$BASE_URL/location/update" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc --argjson lat "$LAT" --argjson lng "$LNG" '{lat: $lat, lng: $lng, accuracy: 10}')"
canary_expect presence "200" "jeeber GPS fix"

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[3/9] device — register a canary FCM token for the jeeber"
canary_log "        Synthetic on purpose: FCM rejecting it still proves the whole"
canary_log "        producer chain reached FCM."
canary_http PUT "$BASE_URL/api/PushNotification/register" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc --arg t "jeeb-canary-fcm-token-$RUN_TAG" --arg d "jeeb-canary-device-$JEEBER_ID" \
    '{fcmToken: $t, deviceId: $d}')"
canary_expect device "200 201" "canary device registration"

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[4/9] request — client creates a Flash-tier request"
TIERS_FILE="$(canary_tmpfile tiers)"
canary_http GET "$BASE_URL/tiers" --out "$TIERS_FILE"
TIER_ID="$(canary_flash_tier_id <"$TIERS_FILE")"
[ -n "$TIER_ID" ] || TIER_ID="flash"
canary_note "flash tier id: $TIER_ID"

# The run tag rides the DESCRIPTION because the notification projection drops the
# request id; the 80-char body preview is the only place the canary can mark.
REQUEST_DESCRIPTION="canary $RUN_TAG automated probe, ignore"
REQUEST_FILE="$(canary_tmpfile request)"
# The fan-out lives ONLY on the V1 create route: legacy POST /requests has no
# NotifyNewRequestAsync caller and seeds no delivery row, so it pushes nothing.
canary_http POST "$BASE_URL/v1/requests" \
  --bearer-var CLIENT_TOKEN \
  --header "Idempotency-Key: jeeb-canary-request-$RUN_TAG" \
  --json "$(jq -nc \
      --arg tier "$TIER_ID" --arg desc "$REQUEST_DESCRIPTION" \
      --argjson plat "$LAT" --argjson plng "$LNG" \
      --argjson dlat "$DROP_LAT" --argjson dlng "$DROP_LNG" '{
        description: $desc,
        tierId: $tier,
        pickupLocation: {lat: $plat, lng: $plng},
        dropoffLocation: {lat: $dlat, lng: $dlng},
        pickupAddress: "Canary pickup (offshore)",
        dropoffAddress: "Canary dropoff (offshore)"
      }')" \
  --out "$REQUEST_FILE"
canary_expect request "200 201" "request create"
if [ "$CANARY_MODE" = "execute" ]; then
  REQUEST_ID="$(jq -r '(.id // .Id // .request.id // "")' <"$REQUEST_FILE" 2>/dev/null)"
  [ -n "$REQUEST_ID" ] || canary_fail request "request create returned no id"
  canary_note "request id: $REQUEST_ID"
else
  REQUEST_ID='<request_id>'
fi

# ---------------------------------------------------------------------------
# Before push on purpose: the ratchet must be named even when push is broken too.
canary_log ""
canary_log "[5/9] chat gate — resolve the conversation (503 here == UseUpstream__Chat OFF)"
CONV_FILE="$(canary_tmpfile conversation)"
canary_http GET "$BASE_URL/v1/chat/jeeb/conversations/by-request/$REQUEST_ID" \
  --bearer-var CLIENT_TOKEN --out "$CONV_FILE"
if [ "$CANARY_MODE" = "execute" ] && [ "$CANARY_LAST_CODE" = "404" ]; then
  canary_note "no conversation yet — creating it the way the app does"
  canary_http POST "$BASE_URL/v1/chat/jeeb/conversations" \
    --bearer-var CLIENT_TOKEN \
    --json "$(jq -nc --arg r "$REQUEST_ID" --arg c "$CLIENT_ID" '{request_id: $r, client_user_id: $c}')" \
    --out "$CONV_FILE"
fi
if [ "$CANARY_MODE" = "execute" ] && [ "$CANARY_LAST_CODE" = "503" ]; then
  canary_fail chat "conversation route returned 503 — FeatureFlags__UseUpstream__Chat is OFF on this gateway (every staging deploy resets it; jeeb-chat-b-activation.yml turns it back on)"
fi
canary_expect chat "200 201" "conversation resolve/create"
if [ "$CANARY_MODE" = "execute" ]; then
  CONVERSATION_ID="$(canary_conversation_id <"$CONV_FILE")"
  [ -n "$CONVERSATION_ID" ] || canary_fail chat "conversation resolve returned no conversation_id"
  canary_note "conversation id: $CONVERSATION_ID"
else
  CONVERSATION_ID='<conversation_id>'
fi

# ---------------------------------------------------------------------------
# chat-service seats ONLY the owner at create; offer+accept is what seats the jeeber.
canary_log ""
canary_log "[6/9] lifecycle — jeeber offers, client accepts (this is what seats the jeeber)"
OFFER_FILE="$(canary_tmpfile offer)"
canary_http POST "$BASE_URL/v1/requests/$REQUEST_ID/offers" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc '{fee: 6, etaMinutes: 20, note: "canary probe"}')" \
  --out "$OFFER_FILE"
canary_expect lifecycle "200 201" "jeeber offer submit (409 out-of-range means the GPS fix did not land)"
if [ "$CANARY_MODE" = "execute" ]; then
  OFFER_ID="$(jq -r '(.id // .Id // .offerId // "")' <"$OFFER_FILE" 2>/dev/null)"
  [ -n "$OFFER_ID" ] || canary_fail lifecycle "offer submit returned no offer id"
  canary_note "offer id: $OFFER_ID"
else
  OFFER_ID='<offer_id>'
fi

if [ "$ACCEPT_OFFER" = "true" ]; then
  canary_http POST "$BASE_URL/v1/offers/$OFFER_ID/accept" \
    --bearer-var CLIENT_TOKEN \
    --header "Idempotency-Key: jeeb-canary-accept-$RUN_TAG" \
    --json '{}'
  canary_expect lifecycle "200 201" "client accepts the canary offer"
  canary_note "jeeber is now the seated winner; conversation phase advanced"
else
  canary_note "accept leg disabled — the jeeber stays a restricted offerer"
fi

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[7/9] chat — send one message, then prove the jeeber can see it"
MESSAGE_FILE="$(canary_tmpfile message)"
# audience MUST be the string "all": the visibility resolver treats a structured
# object as opaque and a restricted offerer would then never see the message.
canary_http POST "$BASE_URL/v1/conversations/$CONVERSATION_ID/messages" \
  --bearer-var CLIENT_TOKEN \
  --header "Idempotency-Key: jeeb-canary-message-$RUN_TAG" \
  --json "$(jq -nc --arg body "canary $RUN_TAG automated probe, ignore" \
    '{kind: "text", subtype: "canary", audience: "all", body: $body}')" \
  --out "$MESSAGE_FILE"
canary_expect chat "200 201" "chat message append"
if [ "$CANARY_MODE" = "execute" ]; then
  MESSAGE_ID="$(canary_message_id <"$MESSAGE_FILE")"
  [ -n "$MESSAGE_ID" ] || canary_fail chat "append returned no message_id"
  canary_note "message id: $MESSAGE_ID"
else
  MESSAGE_ID='<message_id>'
fi

# The visibility lane: chat-service scopes this page to the bearer and echoes
# viewer_id, so a jeeber-side hit proves VisibleTo[] carries the jeeber.
recipient_sees_message() {
  local out; out="$(canary_tmpfile recipient-view)"
  canary_http GET "$BASE_URL/v1/conversations/$CONVERSATION_ID/messages?limit=50" \
    --bearer-var JEEBER_TOKEN --out "$out"
  canary_message_visible "$MESSAGE_ID" "$JEEBER_ID" <"$out"
}
canary_poll chat "$(canary_deadline "$CHAT_BUDGET")" 3 \
  "the jeeber's own message page carries $MESSAGE_ID" -- recipient_sees_message

# Third of the three identifiers that must agree, or the recipient's Firestore
# listener renders nothing: mint uid == app currentUserId == a VisibleTo entry.
FBTOKEN_FILE="$(canary_tmpfile firebase-token)"
canary_http POST "$BASE_URL/v1/chat/firebase-token" \
  --bearer-var JEEBER_TOKEN --json '{}' --no-preview --out "$FBTOKEN_FILE"
canary_expect chat "200" "Firebase custom-token mint for the jeeber"
if [ "$CANARY_MODE" = "execute" ]; then
  MINTED_UID="$(jq -r '(.uid // "")' <"$FBTOKEN_FILE" 2>/dev/null)"
  [ "$MINTED_UID" = "$JEEBER_ID" ] || \
    canary_fail chat "Firebase custom-token uid '$MINTED_UID' != jeeber id '$JEEBER_ID' — the Firestore listener would render nothing"
  canary_note "Firebase uid matches the jeeber id"
fi

if [ -n "${JEEB_FIREBASE_WEB_API_KEY:-}" ]; then
  canary_log "  firestore — running the app's own VisibleTo query as the jeeber"
  CUSTOM_TOKEN="$(jq -r '(.token // "")' <"$FBTOKEN_FILE" 2>/dev/null)"
  EXCHANGE_FILE="$(canary_tmpfile firebase-exchange)"
  # The API key rides X-Goog-Api-Key, never the query string, so it cannot leak
  # into a printed URL, a plan, or a CI log.
  canary_http POST \
    "https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken" \
    --header-var "X-Goog-Api-Key:JEEB_FIREBASE_WEB_API_KEY" \
    --json "$(jq -nc --arg t "${CUSTOM_TOKEN:-}" '{token: $t, returnSecureToken: true}')" \
    --no-preview --out "$EXCHANGE_FILE"
  canary_expect chat "200" "Firebase Identity Toolkit exchange"
  if [ "$CANARY_MODE" = "execute" ]; then
    FIREBASE_ID_TOKEN="$(jq -r '(.idToken // "")' <"$EXCHANGE_FILE" 2>/dev/null)"
    export FIREBASE_ID_TOKEN
    [ -n "$FIREBASE_ID_TOKEN" ] || canary_fail chat "Firebase custom-token exchange returned no idToken"
    canary_mask "$FIREBASE_ID_TOKEN"
  else
    FIREBASE_ID_TOKEN=""; export FIREBASE_ID_TOKEN
  fi
  firestore_sees_message() {
    local out; out="$(canary_tmpfile firestore)"
    canary_http POST \
      "https://firestore.googleapis.com/v1/projects/$FIREBASE_PROJECT_ID/databases/$FIREBASE_DATABASE_ID/documents/Conversations/$CONVERSATION_ID:runQuery" \
      --bearer-var FIREBASE_ID_TOKEN \
      --json "$(canary_firestore_query "$JEEBER_ID" 50)" --out "$out"
    canary_firestore_hit "$MESSAGE_ID" <"$out"
  }
  canary_poll chat "$(canary_deadline "$FIRESTORE_BUDGET")" 3 \
    "the Firestore VisibleTo query returns $MESSAGE_ID" -- firestore_sees_message
else
  canary_log "  firestore — SKIPPED (JEEB_FIREBASE_WEB_API_KEY unset)."
fi

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[8/9] push — poll for a real dispatch outcome for the jeeber"

ledger_probe() {
  local out; out="$(canary_tmpfile ledger)"
  canary_http GET "$PUSH_LEDGER_BASE_URL/api/v1/sent-payload/idempotency?target_user_id=$JEEBER_ID&limit=50" \
    --header "X-Caller-Id: $PUSH_CALLER_ID" \
    --header-var "X-Api-Key:JEEB_PUSH_INTERNAL_API_KEY" \
    --out "$out"
  canary_dispatch_terminal "$JEEBER_ID" "$ALLOW_FCM_TOKEN_REJECT" <"$out"
}

inbox_probe() {
  local out; out="$(canary_tmpfile inbox)"
  canary_http GET "$BASE_URL/v1/notifications?page=1&pageSize=20" \
    --bearer-var JEEBER_TOKEN --out "$out"
  canary_inbox_hit "$RUN_TAG" <"$out"
}

PUSH_DEADLINE="$(canary_deadline "$PUSH_BUDGET")"
if [ -n "$PUSH_LEDGER_BASE_URL" ]; then
  PUSH_PROOF="relay-ledger (terminal push_dispatch state at push-notification)"
  canary_log "  proof: push-notification dispatch ledger, scope gateway.recovery"
  canary_poll push "$PUSH_DEADLINE" 5 "a terminal push_dispatch for $JEEBER_ID" -- ledger_probe
else
  PUSH_PROOF="durable-inbox (gateway -> notification-service durable record ONLY; FCM leg unproven)"
  canary_log "  proof: durable notification inbox — push-notification is not publicly reachable."
  canary_log "  WARNING: this proves the producer chain up to notification-service, NOT the FCM call."
  canary_poll push "$PUSH_DEADLINE" 5 "a new_request notification tagged $RUN_TAG" -- inbox_probe
fi

# ---------------------------------------------------------------------------
canary_log ""
canary_log "[9/9] cleanup — handled by the EXIT trap so a FAILED run cleans up too"

SUMMARY="chat+push canary GREEN on $BASE_URL — request $REQUEST_ID fanned out, push proof: $PUSH_PROOF, message $MESSAGE_ID visible to $JEEBER_ID"
canary_log ""
if [ "$CANARY_MODE" = "execute" ]; then
  canary_log "PASS: $SUMMARY"
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] && printf '%s\n' "$SUMMARY" >>"$GITHUB_STEP_SUMMARY"
else
  canary_log "PLAN COMPLETE — nothing above was executed. Re-run with --execute to assert."
fi
exit 0
