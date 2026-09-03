#!/usr/bin/env bash
#
# End-to-end chat + push OUTCOME canary for the Jeeb staging gateway.
#
# Every large chat/push outage in this fleet passed /health/ready while push was
# 100% dead. This probe therefore asserts outcomes only: a real request fans out
# to a real jeeber, a real push dispatch reaches a terminal state, a real chat
# message becomes visible to the recipient. It never reads a health endpoint as
# evidence of anything.
#
#   scripts/canary/run.sh --base-url https://app.jeeb.fds-1.com --plan
#   scripts/canary/run.sh --base-url https://app.jeeb.fds-1.com --execute
#
# Identity: canary bearers are minted directly at POST /auth/tokens with
# X-Service-Auth-Key ($JEEB_TOKEN_MINT_KEY), exactly like the existing
# durable-idempotency and heartbeat-presence smokes. It deliberately does NOT
# use SuperLogin open mode (a security hole that will be closed) and never
# touches a real user account.
#
# Bash + curl + jq only. No secret is ever printed: plan mode prints '$VAR'.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/canary/lib.sh
. "$SCRIPT_DIR/lib.sh"

BASE_URL="${JEEB_CANARY_BASE_URL:-https://app.jeeb.fds-1.com}"
TIMEOUT="${JEEB_CANARY_TIMEOUT:-150}"
CLIENT_ID="${JEEB_CANARY_CLIENT_ID:-canary-chat-push-client}"
JEEBER_ID="${JEEB_CANARY_JEEBER_ID:-canary-chat-push-jeeber}"
LAT="${JEEB_CANARY_LAT:-33.8886}"
LNG="${JEEB_CANARY_LNG:-35.4955}"
DROP_LAT="${JEEB_CANARY_DROP_LAT:-33.8959}"
DROP_LNG="${JEEB_CANARY_DROP_LNG:-35.4784}"
AVAIL_PREFIX="${JEEB_CANARY_AVAILABILITY_PREFIX:-/v1}"
PUSH_LEDGER_BASE_URL="${PUSH_LEDGER_BASE_URL:-}"
PUSH_CALLER_ID="${JEEB_PUSH_CALLER_ID:-jeeb-gateway}"
FIREBASE_PROJECT_ID="${JEEB_FIREBASE_PROJECT_ID:-jeeb-5a293}"
FIREBASE_DATABASE_ID="${JEEB_FIREBASE_DATABASE_ID:-(default)}"
ALLOW_FCM_TOKEN_REJECT="${JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT:-true}"
CANARY_MODE=plan

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) BASE_URL="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --client-id) CLIENT_ID="$2"; shift 2 ;;
    --jeeber-id) JEEBER_ID="$2"; shift 2 ;;
    --plan) CANARY_MODE=plan; shift ;;
    --execute) CANARY_MODE=execute; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
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
trap 'rm -rf "$CANARY_WORKDIR"' EXIT

RUN_TAG="${GITHUB_RUN_ID:-local-$(date +%s)}"
DEADLINE=$(( $(date +%s) + TIMEOUT ))
PUSH_PROOF="none"

canary_require_tools

canary_log "== jeeb chat+push canary =="
canary_log "  mode        : $CANARY_MODE"
canary_log "  base url    : $BASE_URL"
canary_log "  client id   : $CLIENT_ID"
canary_log "  jeeber id   : $JEEBER_ID"
canary_log "  pickup      : $LAT,$LNG (fixed Beirut coordinate)"
canary_log "  budget      : ${TIMEOUT}s"
canary_log "  push ledger : ${PUSH_LEDGER_BASE_URL:-<unset — falling back to the durable notification inbox>}"
if [ -n "${JEEB_FIREBASE_WEB_API_KEY:-}" ]; then
  canary_log "  firestore   : enabled (web API key present)"
else
  canary_log "  firestore   : disabled (JEEB_FIREBASE_WEB_API_KEY unset)"
fi

# ---------------------------------------------------------------------------
# Leg 0 — preflight
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[0/7] preflight"
if [ "$CANARY_MODE" = "execute" ] && [ -z "${JEEB_TOKEN_MINT_KEY:-}" ]; then
  canary_fail preflight "JEEB_TOKEN_MINT_KEY is unset — the canary mints its own bearers and cannot run without it"
fi
canary_http GET "$BASE_URL/health/live"
canary_expect preflight "200" "gateway liveness"

# ---------------------------------------------------------------------------
# Leg 1 — identity: two fixed canary bearers, minted, never super-login
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[1/7] identity — mint the two fixed canary bearers"
# Sets MINTED_TOKEN rather than echoing: a command substitution here would
# swallow the plan output instead of printing it.
MINTED_TOKEN=""
mint_bearer() {
  local user="$1" role="$2" out
  out="$(canary_tmpfile "mint-$role")"
  canary_http POST "$BASE_URL/auth/tokens" \
    --header-var "X-Service-Auth-Key:JEEB_TOKEN_MINT_KEY" \
    --json "$(jq -nc --arg u "$user" --arg r "$role" '{userId: $u, roles: [$r]}')" \
    --out "$out"
  canary_expect identity "200" "$role bearer mint"
  MINTED_TOKEN="$(canary_access_token <"$out")"
}
mint_bearer "$CLIENT_ID" client; CLIENT_TOKEN="$MINTED_TOKEN"
mint_bearer "$JEEBER_ID" jeeber; JEEBER_TOKEN="$MINTED_TOKEN"
export CLIENT_TOKEN JEEBER_TOKEN
if [ "$CANARY_MODE" = "execute" ]; then
  [ -n "$CLIENT_TOKEN" ] || canary_fail identity "client mint returned no accessToken"
  [ -n "$JEEBER_TOKEN" ] || canary_fail identity "jeeber mint returned no accessToken"
  printf '::add-mask::%s\n' "$CLIENT_TOKEN" "$JEEBER_TOKEN"
  canary_note "both bearers minted (values masked)"
fi

# ---------------------------------------------------------------------------
# Leg 2 — presence: fan-out is geo-filtered fail-closed, so replicate the app
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[2/7] presence — availability ON + a GPS fix at the pickup coordinate"
canary_http PUT "$BASE_URL$AVAIL_PREFIX/jeebers/me/availability" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc '{online: true, vehicleType: "car", zone: "beirut-central"}')"
canary_expect presence "200" "jeeber go-online"

canary_http POST "$BASE_URL/location/update" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc --argjson lat "$LAT" --argjson lng "$LNG" '{lat: $lat, lng: $lng, accuracy: 10}')"
canary_expect presence "200" "jeeber GPS fix"

# ---------------------------------------------------------------------------
# Leg 3 — a device seat for the jeeber, so a push dispatch row can exist at all
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[3/7] device — register a canary FCM token for the jeeber"
canary_log "        (the token is deliberately synthetic: FCM rejecting it still proves"
canary_log "         the producer chain gateway -> notification-service -> relay -> FCM)"
canary_http PUT "$BASE_URL/api/PushNotification/register" \
  --bearer-var JEEBER_TOKEN \
  --json "$(jq -nc --arg t "jeeb-canary-fcm-token-$RUN_TAG" --arg d "jeeb-canary-device-$JEEBER_ID" \
    '{fcmToken: $t, deviceId: $d}')"
canary_expect device "200|201" "canary device registration"

# ---------------------------------------------------------------------------
# Leg 4 — the client creates a Flash request at the fixed Beirut coordinate
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[4/7] request — client creates a Flash-tier request"
TIERS_FILE="$(canary_tmpfile tiers)"
canary_http GET "$BASE_URL/tiers" --out "$TIERS_FILE"
TIER_ID="$(canary_flash_tier_id <"$TIERS_FILE")"
[ -n "$TIER_ID" ] || TIER_ID="flash"
canary_note "flash tier id: $TIER_ID"

REQUEST_IDEM="jeeb-canary-request-$RUN_TAG"
REQUEST_FILE="$(canary_tmpfile request)"
canary_http POST "$BASE_URL/requests" \
  --bearer-var CLIENT_TOKEN \
  --header "Idempotency-Key: $REQUEST_IDEM" \
  --json "$(jq -nc \
      --arg tier "$TIER_ID" --arg tag "canary" \
      --argjson plat "$LAT" --argjson plng "$LNG" \
      --argjson dlat "$DROP_LAT" --argjson dlng "$DROP_LNG" '{
        description: "canary — automated chat+push outcome probe, ignore",
        tierId: $tier,
        pickupLocation: {lat: $plat, lng: $plng},
        dropoffLocation: {lat: $dlat, lng: $dlng},
        pickupAddress: "Beirut canary pickup",
        dropoffAddress: "Beirut canary dropoff",
        tags: [$tag]
      }')" \
  --out "$REQUEST_FILE"
canary_expect request "200|201" "request create"
REQUEST_ID="$(jq -r '(.id // .Id // .request.id // "")' <"$REQUEST_FILE" 2>/dev/null)"
if [ "$CANARY_MODE" = "execute" ]; then
  [ -n "$REQUEST_ID" ] || canary_fail request "request create returned no id"
  canary_note "request id: $REQUEST_ID"
else
  REQUEST_ID='<request_id>'
fi

# ---------------------------------------------------------------------------
# Leg 5 — the push OUTCOME
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[5/7] push — poll for a real dispatch outcome for the jeeber"

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
  canary_inbox_hit "$REQUEST_ID" <"$out"
}

if [ -n "$PUSH_LEDGER_BASE_URL" ]; then
  PUSH_PROOF="relay-ledger (terminal push_dispatch state at push-notification)"
  canary_log "  proof: push-notification dispatch ledger, scope gateway.recovery"
  canary_poll push "$DEADLINE" 5 "a terminal push_dispatch for $JEEBER_ID" -- ledger_probe
else
  PUSH_PROOF="durable-inbox (gateway -> notification-service durable record ONLY; FCM leg unproven)"
  canary_log "  proof: durable notification inbox — push-notification is not publicly reachable."
  canary_log "  WARNING: this proves the producer chain up to notification-service, NOT the FCM call."
  canary_log "  Set PUSH_LEDGER_BASE_URL (see docs/runbooks/chat-push-canary.md) for the full leg."
  canary_poll push "$DEADLINE" 5 "a durable notification for the canary request" -- inbox_probe
fi

# ---------------------------------------------------------------------------
# Leg 6 — the chat OUTCOME, asserted from the RECIPIENT's side
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[6/7] chat — send one message, then prove the jeeber can see it"

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
# 503 here is the UseUpstream__Chat ratchet, the single most common chat outage.
if [ "$CANARY_MODE" = "execute" ] && [ "$CANARY_LAST_CODE" = "503" ]; then
  canary_fail chat "conversation route returned 503 — FeatureFlags__UseUpstream__Chat is OFF on this gateway (every staging deploy resets it; jeeb-chat-b-activation.yml turns it back on)"
fi
canary_expect chat "200|201" "conversation resolve/create"
CONVERSATION_ID="$(canary_conversation_id <"$CONV_FILE")"
if [ "$CANARY_MODE" = "execute" ]; then
  [ -n "$CONVERSATION_ID" ] || canary_fail chat "conversation resolve returned no conversation_id"
  canary_note "conversation id: $CONVERSATION_ID"
else
  CONVERSATION_ID='<conversation_id>'
fi

MESSAGE_IDEM="jeeb-canary-message-$RUN_TAG"
MESSAGE_FILE="$(canary_tmpfile message)"
canary_http POST "$BASE_URL/v1/conversations/$CONVERSATION_ID/messages" \
  --bearer-var CLIENT_TOKEN \
  --header "Idempotency-Key: $MESSAGE_IDEM" \
  --json "$(jq -nc --arg body "canary $RUN_TAG — automated probe, ignore" \
    '{kind: "text", subtype: "canary", body: $body, audience: {scope: "conversation"}}')" \
  --out "$MESSAGE_FILE"
canary_expect chat "200|201" "chat message append"
MESSAGE_ID="$(canary_message_id <"$MESSAGE_FILE")"
if [ "$CANARY_MODE" = "execute" ]; then
  [ -n "$MESSAGE_ID" ] || canary_fail chat "append returned no message_id"
  canary_note "message id: $MESSAGE_ID"
else
  MESSAGE_ID='<message_id>'
fi

# The visibility lane: chat-service scopes this page to the bearer, and the
# response echoes viewer_id. A jeeber-side hit proves VisibleTo carries them.
recipient_sees_message() {
  local out; out="$(canary_tmpfile recipient-view)"
  canary_http GET "$BASE_URL/v1/conversations/$CONVERSATION_ID/messages?limit=50" \
    --bearer-var JEEBER_TOKEN --out "$out"
  canary_message_visible "$MESSAGE_ID" "$JEEBER_ID" <"$out"
}
canary_poll chat "$DEADLINE" 3 "the jeeber's own message page carries $MESSAGE_ID" -- recipient_sees_message

# The three identifiers that must agree or the recipient silently sees nothing:
# gateway mint uid == app currentUserId == an element of chat-service VisibleTo.
FBTOKEN_FILE="$(canary_tmpfile firebase-token)"
canary_http POST "$BASE_URL/v1/chat/firebase-token" \
  --bearer-var JEEBER_TOKEN --json '{}' --out "$FBTOKEN_FILE"
canary_expect chat "200" "Firebase custom-token mint for the jeeber"
if [ "$CANARY_MODE" = "execute" ]; then
  MINTED_UID="$(jq -r '(.uid // "")' <"$FBTOKEN_FILE" 2>/dev/null)"
  [ "$MINTED_UID" = "$JEEBER_ID" ] || \
    canary_fail chat "Firebase custom-token uid '$MINTED_UID' != jeeber id '$JEEBER_ID' — the Firestore listener would render nothing"
  canary_note "Firebase uid matches the jeeber id"
fi

# Optional: assert the document itself through the SAME query the app runs.
if [ -n "${JEEB_FIREBASE_WEB_API_KEY:-}" ]; then
  canary_log "  firestore — running the app's own VisibleTo query as the jeeber"
  CUSTOM_TOKEN="$(jq -r '(.token // "")' <"$FBTOKEN_FILE" 2>/dev/null)"
  EXCHANGE_FILE="$(canary_tmpfile firebase-exchange)"
  # The API key rides X-Goog-Api-Key, not the query string, so it never appears
  # in a printed URL, a plan, or a CI log.
  canary_http POST \
    "https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken" \
    --header-var "X-Goog-Api-Key:JEEB_FIREBASE_WEB_API_KEY" \
    --json "$(jq -nc --arg t "${CUSTOM_TOKEN:-}" '{token: $t, returnSecureToken: true}')" \
    --out "$EXCHANGE_FILE"
  if [ "$CANARY_MODE" = "execute" ]; then
    FIREBASE_ID_TOKEN="$(jq -r '(.idToken // "")' <"$EXCHANGE_FILE" 2>/dev/null)"
    export FIREBASE_ID_TOKEN
    [ -n "$FIREBASE_ID_TOKEN" ] || canary_fail chat "Firebase custom-token exchange returned no idToken"
    printf '::add-mask::%s\n' "$FIREBASE_ID_TOKEN"
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
  canary_poll chat "$DEADLINE" 3 "the Firestore VisibleTo query returns $MESSAGE_ID" -- firestore_sees_message
else
  canary_log "  firestore — SKIPPED (JEEB_FIREBASE_WEB_API_KEY unset)."
  canary_log "              With it, the canary signs in as the jeeber uid and runs the app's own"
  canary_log "              Conversations/{id}/Messages where VisibleTo array-contains uid query."
  canary_log "              No service account is needed; the security rules do the proving."
fi

# ---------------------------------------------------------------------------
# Leg 7 — cleanup: leave a bounded trail
# ---------------------------------------------------------------------------
canary_log ""
canary_log "[7/7] cleanup — cancel the canary request"
canary_http PUT "$BASE_URL$AVAIL_PREFIX/jeebers/me/availability" \
  --bearer-var JEEBER_TOKEN --json '{"online": false}'
canary_http DELETE "$BASE_URL/requests/$REQUEST_ID" --bearer-var CLIENT_TOKEN
if [ "$CANARY_MODE" = "execute" ]; then
  case "$CANARY_LAST_CODE" in
    204|404|409) canary_note "request closed out (HTTP $CANARY_LAST_CODE)" ;;
    *) canary_log "  ::warning::cleanup cancel returned HTTP $CANARY_LAST_CODE — a canary request may be left open" ;;
  esac
fi

# ---------------------------------------------------------------------------
SUMMARY="chat+push canary GREEN on $BASE_URL — request $REQUEST_ID fanned out, push proof: $PUSH_PROOF, message $MESSAGE_ID visible to $JEEBER_ID"
canary_log ""
if [ "$CANARY_MODE" = "execute" ]; then
  canary_log "PASS: $SUMMARY"
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] && printf '%s\n' "$SUMMARY" >>"$GITHUB_STEP_SUMMARY"
else
  canary_log "PLAN COMPLETE — nothing above was executed. Re-run with --execute to assert."
fi
exit 0
