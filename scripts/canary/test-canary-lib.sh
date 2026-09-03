#!/usr/bin/env bash
#
# Offline unit tests for the canary's non-trivial jq/bash logic and for the
# plan-mode contract (a plan must execute nothing and print no secret).
# No network. Run: bash scripts/canary/test-canary-lib.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FAILURES=0
CASES=0

check() {
  local label="$1" expected="$2" actual="$3"
  CASES=$((CASES + 1))
  if [ "$expected" = "$actual" ]; then
    printf 'ok   %s\n' "$label"
  else
    printf 'FAIL %s\n  expected: %s\n  actual  : %s\n' "$label" "$expected" "$actual"
    FAILURES=$((FAILURES + 1))
  fi
}

check_exit() {
  local label="$1" expected="$2"; shift 2
  "$@" >/dev/null 2>&1
  check "$label" "$expected" "$?"
}

CANARY_MODE=plan
# shellcheck source=scripts/canary/lib.sh
. "$SCRIPT_DIR/lib.sh"

# --- canary_flash_tier_id ---------------------------------------------------
check "flash tier id is picked out of the catalog" "0be308ce" \
  "$(printf '%s' '{"items":[{"id":"x","name":"Standard"},{"id":"0be308ce","name":"Flash"}]}' | canary_flash_tier_id)"
check "flash matching is case-insensitive" "abc" \
  "$(printf '%s' '{"items":[{"id":"abc","name":"flash"}]}' | canary_flash_tier_id)"
check "a catalog with no Flash row yields empty" "" \
  "$(printf '%s' '{"items":[{"id":"x","name":"Standard"}]}' | canary_flash_tier_id)"
check "an empty plan-mode body yields empty" "" \
  "$(printf '%s' '{}' | canary_flash_tier_id)"

# --- canary_access_token ----------------------------------------------------
check "camelCase accessToken is read" "tok" \
  "$(printf '%s' '{"accessToken":"tok"}' | canary_access_token)"
check "snake_case access_token is read" "tok" \
  "$(printf '%s' '{"access_token":"tok"}' | canary_access_token)"
check "a mint response without a token yields empty" "" \
  "$(printf '%s' '{"error":"nope"}' | canary_access_token)"

# --- canary_conversation_id / canary_message_id -----------------------------
check "conversation_id is read from the snake_case wire" "c1" \
  "$(printf '%s' '{"conversation_id":"c1"}' | canary_conversation_id)"
check "message_id is read from the snake_case wire" "m1" \
  "$(printf '%s' '{"message_id":"m1"}' | canary_message_id)"

# --- canary_message_visible -------------------------------------------------
VISIBLE='{"conversation_id":"c1","viewer_id":"jeeber-1","messages":[{"message_id":"m1"},{"message_id":"m2"}]}'
check_exit "the recipient's own page carrying the message passes" 0 \
  bash -c "printf '%s' '$VISIBLE' | { . '$SCRIPT_DIR/lib.sh'; canary_message_visible m2 jeeber-1; }"
check_exit "a page missing the message fails" 1 \
  bash -c "printf '%s' '$VISIBLE' | { . '$SCRIPT_DIR/lib.sh'; canary_message_visible m9 jeeber-1; }"
check_exit "a page scoped to a different viewer fails" 1 \
  bash -c "printf '%s' '$VISIBLE' | { . '$SCRIPT_DIR/lib.sh'; canary_message_visible m1 someone-else; }"
check_exit "an empty plan-mode body fails" 1 \
  bash -c "printf '{}' | { . '$SCRIPT_DIR/lib.sh'; canary_message_visible m1 jeeber-1; }"

# --- canary_dispatch_terminal ----------------------------------------------
SUCCEEDED='{"items":[{"target_user_id":"jeeber-1","state":"succeeded"}]}'
CLAIMED='{"items":[{"target_user_id":"jeeber-1","state":"claimed"}]}'
FAILED='{"items":[{"target_user_id":"jeeber-1","state":"failed"}]}'
OTHER='{"items":[{"target_user_id":"someone-else","state":"succeeded"}]}'
check_exit "a succeeded dispatch for the jeeber passes" 0 \
  bash -c "printf '%s' '$SUCCEEDED' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 true; }"
check_exit "a dispatch still claimed fails — this is the silent-push shape" 1 \
  bash -c "printf '%s' '$CLAIMED' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 true; }"
check_exit "an FCM rejection still proves the producer chain when allowed" 0 \
  bash -c "printf '%s' '$FAILED' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 true; }"
check_exit "an FCM rejection fails when rejections are not allowed" 1 \
  bash -c "printf '%s' '$FAILED' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 false; }"
check_exit "another user's dispatch never counts" 1 \
  bash -c "printf '%s' '$OTHER' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 true; }"
check_exit "an empty ledger fails" 1 \
  bash -c "printf '%s' '{\"items\":[]}' | { . '$SCRIPT_DIR/lib.sh'; canary_dispatch_terminal jeeber-1 true; }"

# --- canary_inbox_hit -------------------------------------------------------
check_exit "an inbox row naming the request passes" 0 \
  bash -c "printf '%s' '{\"items\":[{\"title\":\"New request req-7\"}]}' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit req-7; }"
check_exit "an inbox with no matching row fails" 1 \
  bash -c "printf '%s' '{\"items\":[{\"title\":\"New request req-1\"}]}' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit req-7; }"

# --- canary_firestore_hit ---------------------------------------------------
HIT='[{"document":{"name":"projects/jeeb-5a293/databases/(default)/documents/Conversations/c1/Messages/m1"}}]'
EMPTY='[{"readTime":"2026-09-04T00:00:00Z"}]'
check_exit "a runQuery result carrying the document passes" 0 \
  bash -c "printf '%s' '$HIT' | { . '$SCRIPT_DIR/lib.sh'; canary_firestore_hit m1; }"
check_exit "a runQuery result for a different document fails" 1 \
  bash -c "printf '%s' '$HIT' | { . '$SCRIPT_DIR/lib.sh'; canary_firestore_hit m2; }"
check_exit "an empty runQuery result fails" 1 \
  bash -c "printf '%s' '$EMPTY' | { . '$SCRIPT_DIR/lib.sh'; canary_firestore_hit m1; }"

# --- canary_firestore_query mirrors the app's listener -----------------------
QUERY="$(canary_firestore_query jeeber-1 50)"
check "the query filters Messages on VisibleTo array-contains uid" \
  "Messages ARRAY_CONTAINS VisibleTo jeeber-1 CreatedAt DESCENDING 50" \
  "$(printf '%s' "$QUERY" | jq -r '.structuredQuery
     | "\(.from[0].collectionId) \(.where.fieldFilter.op) \(.where.fieldFilter.field.fieldPath) \(.where.fieldFilter.value.stringValue) \(.orderBy[0].field.fieldPath) \(.orderBy[0].direction) \(.limit)"')"

# --- plan mode executes nothing and prints no secret ------------------------
PLAN_OUT="$(JEEB_TOKEN_MINT_KEY=super-secret-value \
  bash "$SCRIPT_DIR/run.sh" --base-url https://app.jeeb.fds-1.com --plan 2>&1)"
check "plan mode exits 0" "0" "$?"
case "$PLAN_OUT" in
  *super-secret-value*) check "plan mode never prints a secret" "absent" "PRESENT" ;;
  *) check "plan mode never prints a secret" "absent" "absent" ;;
esac
case "$PLAN_OUT" in
  *'$JEEB_TOKEN_MINT_KEY'*) check "plan mode names the secret variable instead" "named" "named" ;;
  *) check "plan mode names the secret variable instead" "named" "MISSING" ;;
esac
case "$PLAN_OUT" in
  *'PLAN COMPLETE'*) check "plan mode reaches the end without asserting" "done" "done" ;;
  *) check "plan mode reaches the end without asserting" "done" "TRUNCATED" ;;
esac
case "$PLAN_OUT" in
  *'  CALL '*) check "plan mode issues no live call" "none" "EXECUTED" ;;
  *) check "plan mode issues no live call" "none" "none" ;;
esac
for leg in 'v1/chat/jeeb/conversations/by-request' 'v1/conversations/' 'requests' 'location/update' 'api/PushNotification/register' 'auth/tokens'; do
  case "$PLAN_OUT" in
    *"$leg"*) check "plan covers $leg" "covered" "covered" ;;
    *) check "plan covers $leg" "covered" "MISSING" ;;
  esac
done

printf '\n%s case(s), %s failure(s)\n' "$CASES" "$FAILURES"
[ "$FAILURES" -eq 0 ] || exit 1
printf 'canary lib contract holds.\n'
