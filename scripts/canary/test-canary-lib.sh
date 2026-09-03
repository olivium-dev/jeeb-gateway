#!/usr/bin/env bash
# Offline unit tests for the canary's jq/bash logic and its plan-mode contract.
# No network, no secret. Run: bash scripts/canary/test-canary-lib.sh

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

# canary_deadline reads the clock itself, so an exact compare races the second
# boundary; assert a window instead.
check_range() {
  local label="$1" lo="$2" hi="$3" actual="$4"
  CASES=$((CASES + 1))
  if [ "$actual" -ge "$lo" ] && [ "$actual" -le "$hi" ]; then
    printf 'ok   %s\n' "$label"
  else
    printf 'FAIL %s\n  expected: %s..%s\n  actual  : %s\n' "$label" "$lo" "$hi" "$actual"
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
CANARY_TEST_TMP="$(mktemp -d)"
trap 'rm -rf "$CANARY_TEST_TMP"' EXIT

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

# --- canary_inbox_hit (the projection drops the request id, so the run tag
# --- rides the 80-char description preview in `body`) -----------------------
INBOX_HIT='{"items":[{"type":"new_request","title":"New delivery request","body":"canary run-77 automated probe, ignore"}]}'
INBOX_OTHER_RUN='{"items":[{"type":"new_request","title":"New delivery request","body":"canary run-12 automated probe, ignore"}]}'
INBOX_OTHER_TYPE='{"items":[{"type":"chat_message","title":"x","body":"canary run-77 automated probe, ignore"}]}'
check_exit "a new_request row whose body carries the run tag passes" 0 \
  bash -c "printf '%s' '$INBOX_HIT' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit run-77; }"
check_exit "another run's new_request row never counts" 1 \
  bash -c "printf '%s' '$INBOX_OTHER_RUN' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit run-77; }"
check_exit "a non-new_request row carrying the tag never counts" 1 \
  bash -c "printf '%s' '$INBOX_OTHER_TYPE' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit run-77; }"
check_exit "a request id is NOT matched — the projection drops it" 1 \
  bash -c "printf '%s' '$INBOX_HIT' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit 9f2c-request-id; }"
check_exit "an empty inbox fails" 1 \
  bash -c "printf '%s' '{\"items\":[]}' | { . '$SCRIPT_DIR/lib.sh'; canary_inbox_hit run-77; }"

# --- canary_status_accepted / canary_expect (EXECUTE mode). Regression gate:
# --- a `|` from parameter expansion is literal, so `case $code in $want)` never matched.
check_exit "a single status matches" 0 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 200 '200'"
check_exit "a pipe-separated want matches its first code" 0 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 200 '200|201'"
check_exit "a pipe-separated want matches its second code" 0 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 201 '200|201'"
check_exit "a space-separated want matches its second code" 0 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 201 '200 201'"
check_exit "an unlisted status does not match" 1 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 403 '200 201'"
check_exit "a partial-digit status does not match" 1 \
  bash -c ". '$SCRIPT_DIR/lib.sh'; canary_status_accepted 20 '200 201'"

# canary_expect must PASS in execute mode on an accepted multi-status want...
check_exit "canary_expect passes on 201 against a 200 201 want, in EXECUTE mode" 0 \
  bash -c "CANARY_MODE=execute; . '$SCRIPT_DIR/lib.sh'; CANARY_MODE=execute; CANARY_LAST_CODE=201; CANARY_LAST_BODY_FILE=/dev/null; canary_expect device '200 201' 'registration'"
# ...and FAIL on a code outside it, naming the leg.
EXPECT_FAIL="$(bash -c "CANARY_MODE=execute; . '$SCRIPT_DIR/lib.sh'; CANARY_MODE=execute; CANARY_LAST_CODE=403; CANARY_LAST_BODY_FILE=/dev/null; canary_expect device '200 201' 'registration'" 2>&1)"
check_exit "canary_expect exits 1 on an unaccepted status" 1 \
  bash -c "CANARY_MODE=execute; . '$SCRIPT_DIR/lib.sh'; CANARY_LAST_CODE=403; CANARY_LAST_BODY_FILE=/dev/null; canary_expect device '200 201' 'registration'"
case "$EXPECT_FAIL" in
  *'leg [device]'*) check "the failure names the leg that died" "named" "named" ;;
  *) check "the failure names the leg that died" "named" "MISSING" ;;
esac
EXPECT_000="$(bash -c "CANARY_MODE=execute; . '$SCRIPT_DIR/lib.sh'; CANARY_MODE=execute; CANARY_LAST_CODE=000; CANARY_LAST_BODY_FILE=/dev/null; canary_expect push '200' 'ledger read'" 2>&1)"
case "$EXPECT_000" in
  *"NO HTTP response"*) check "a 000 status is reported as a transport failure" "yes" "yes" ;;
  *) check "a 000 status is reported as a transport failure" "yes" "no" ;;
esac

# --- canary_body_preview redacts JWTs. The fixture is BUILT, never written
# --- literally: a literal JWT in the tree is a gitleaks finding even when fake.
b64url() { printf '%s' "$1" | base64 | tr -d '=\n' | tr '/+' '_-'; }
FAKE_JWT="$(b64url '{"alg":"none"}').$(b64url '{"sub":"canary"}').$(b64url 'not-a-signature')"
JWT_BODY="$CANARY_TEST_TMP/jwt-body.json"
printf '{"accessToken":"%s","userId":"0be308ce-01b5-5cb9-a3e8-9adb60668d9c"}' "$FAKE_JWT" >"$JWT_BODY"
PREVIEW="$(canary_body_preview "$JWT_BODY")"
case "$PREVIEW" in
  *eyJ*) check "the preview never leaks a JWT" "redacted" "LEAKED" ;;
  *) check "the preview never leaks a JWT" "redacted" "redacted" ;;
esac
case "$PREVIEW" in
  *0be308ce-01b5-5cb9-a3e8-9adb60668d9c*) check "the preview keeps GUIDs readable" "kept" "kept" ;;
  *) check "the preview keeps GUIDs readable" "kept" "LOST" ;;
esac

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
for leg in 'v1/chat/jeeb/conversations/by-request' 'v1/conversations/' 'location/update' 'api/PushNotification/register' 'auth/tokens' '/offers' '/accept' 'v1/notifications'; do
  case "$PLAN_OUT" in
    *"$leg"*) check "plan covers $leg" "covered" "covered" ;;
    *) check "plan covers $leg" "covered" "MISSING" ;;
  esac
done

# The create route is asserted BY VALUE: only POST /v1/requests fans out, and the
# legacy POST /requests silently pushes nothing.
case "$PLAN_OUT" in
  *"-X POST"*"'https://app.jeeb.fds-1.com/v1/requests'"*) check "the plan creates the request on the V1 route" "v1" "v1" ;;
  *) check "the plan creates the request on the V1 route" "v1" "MISSING" ;;
esac
case "$PLAN_OUT" in
  *"'https://app.jeeb.fds-1.com/requests'"*) check "the plan never uses the legacy create route" "absent" "PRESENT" ;;
  *) check "the plan never uses the legacy create route" "absent" "absent" ;;
esac

# --- canary_deadline is clamped by the whole-run cap, so --timeout is enforced
NOW="$(date +%s)"
check_range "a per-leg budget inside the cap is used as-is" "$((NOW + 30))" "$((NOW + 33))" \
  "$(CANARY_HARD_DEADLINE=$((NOW + 300)) bash -c ". '$SCRIPT_DIR/lib.sh'; canary_deadline 30")"
# The clamped answer IS the cap, so this one is exact — no clock in the result.
check "a per-leg budget beyond the cap is clamped to exactly the cap" "$((NOW + 5))" \
  "$(CANARY_HARD_DEADLINE=$((NOW + 5)) bash -c ". '$SCRIPT_DIR/lib.sh'; canary_deadline 600")"
check_range "no cap means no clamp" "$((NOW + 60))" "$((NOW + 63))" \
  "$(bash -c ". '$SCRIPT_DIR/lib.sh'; canary_deadline 60")"

# --- canary_mask only emits the Actions directive inside Actions
check "canary_mask prints nothing outside GitHub Actions" "" \
  "$(CANARY_MODE=execute bash -c "unset GITHUB_ACTIONS; . '$SCRIPT_DIR/lib.sh'; canary_mask a-real-bearer")"
check "canary_mask emits the directive inside GitHub Actions" "::add-mask::a-real-bearer" \
  "$(CANARY_MODE=execute GITHUB_ACTIONS=true bash -c ". '$SCRIPT_DIR/lib.sh'; canary_mask a-real-bearer")"
check "canary_mask prints nothing in plan mode" "" \
  "$(CANARY_MODE=plan GITHUB_ACTIONS=true bash -c ". '$SCRIPT_DIR/lib.sh'; canary_mask a-real-bearer")"

printf '\n%s case(s), %s failure(s)\n' "$CASES" "$FAILURES"
[ "$FAILURES" -eq 0 ] || exit 1
printf 'canary lib contract holds.\n'
