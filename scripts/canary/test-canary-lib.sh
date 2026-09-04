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

# --- canary_presence_fix_landed ---------------------------------------------
# NULL_ROW is the byte shape live staging returned to the pre-fix canary: HTTP 200,
# online true, and no coordinates at all — a presence row fan-out can never match.
NULL_ROW='{"userId":"canary-chat-push-jeeber","online":true,"vehicleType":"car","zone":"beirut-central","longitude":null,"latitude":null}'
FIXED_ROW='{"userId":"canary-chat-push-jeeber","online":true,"longitude":35.2,"latitude":33.95}'
ELSEWHERE_ROW='{"userId":"canary-chat-push-jeeber","online":true,"longitude":35.5018,"latitude":33.8938}'
check_exit "a row echoing the requested fix passes" 0 \
  bash -c "printf '%s' '$FIXED_ROW' | { . '$SCRIPT_DIR/lib.sh'; canary_presence_fix_landed 33.9500 35.2000; }"
check_exit "a 200 row with null coordinates FAILS — the vacuous-pass shape" 1 \
  bash -c "printf '%s' '$NULL_ROW' | { . '$SCRIPT_DIR/lib.sh'; canary_presence_fix_landed 33.9500 35.2000; }"
check_exit "a row at a different coordinate fails" 1 \
  bash -c "printf '%s' '$ELSEWHERE_ROW' | { . '$SCRIPT_DIR/lib.sh'; canary_presence_fix_landed 33.9500 35.2000; }"
check_exit "an empty plan-mode body fails the presence assertion" 1 \
  bash -c "printf '{}' | { . '$SCRIPT_DIR/lib.sh'; canary_presence_fix_landed 33.9500 35.2000; }"

# --- canary_warn is non-fatal by construction -------------------------------
check "canary_warn emits an Actions annotation" "::warning::ingest is down" \
  "$(CANARY_MODE=execute bash -c ". '$SCRIPT_DIR/lib.sh'; canary_warn 'ingest is down'" 2>&1 >/dev/null)"
check_exit "canary_warn returns 0 so the run continues" 0 \
  bash -c "CANARY_MODE=execute; . '$SCRIPT_DIR/lib.sh'; canary_warn 'ingest is down'"

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

# Presence asserted BY VALUE: the app's only location upload is the availability
# body, and a body without coordinates leaves fan-out nothing to match.
case "$PLAN_OUT" in
  *'"latitude":33.95'*'"longitude":35.2'*) check "the availability body carries the GPS fix" "carried" "carried" ;;
  *) check "the availability body carries the GPS fix" "carried" "MISSING" ;;
esac
case "$PLAN_OUT" in
  *'gps stream  : false'*) check "the GPS batch ingest is probed, not required, by default" "false" "false" ;;
  *) check "the GPS batch ingest is probed, not required, by default" "false" "MISSING" ;;
esac
PLAN_REQUIRED="$(JEEB_TOKEN_MINT_KEY=super-secret-value JEEB_CANARY_REQUIRE_GPS_STREAM=true \
  bash "$SCRIPT_DIR/run.sh" --base-url https://app.jeeb.fds-1.com --plan 2>&1)"
case "$PLAN_REQUIRED" in
  *'gps stream  : true'*) check "the GPS batch ingest can be made required again" "true" "true" ;;
  *) check "the GPS batch ingest can be made required again" "true" "MISSING" ;;
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

# --- canary_wallet_sufficient ------------------------------------------------
check_exit "a funded wallet clears the offer guard" 0 \
  bash -c "printf '%s' '{\"availableBalance\":40.0}' | { . '$SCRIPT_DIR/lib.sh'; canary_wallet_sufficient 0.60; }"
check_exit "a balance exactly at the threshold clears it" 0 \
  bash -c "printf '%s' '{\"availableBalance\":0.60}' | { . '$SCRIPT_DIR/lib.sh'; canary_wallet_sufficient 0.60; }"
check_exit "a balance below the threshold does not" 1 \
  bash -c "printf '%s' '{\"availableBalance\":0.10}' | { . '$SCRIPT_DIR/lib.sh'; canary_wallet_sufficient 0.60; }"
check_exit "an empty wallet does not" 1 \
  bash -c "printf '%s' '{}' | { . '$SCRIPT_DIR/lib.sh'; canary_wallet_sufficient 0.60; }"

# --- the canary identities must be well-formed UUIDs: ban-service rejects
# --- anything else and [RequireActiveUser] then fails closed with 503.
UUID_RE='[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
for role in 'client id' 'jeeber id'; do
  value="$(printf '%s' "$PLAN_OUT" | grep -E "^  $role +: " | sed 's/.*: //' | tr -d ' ')"
  if printf '%s' "$value" | grep -qE "^$UUID_RE\$"; then
    check "the default $role is a well-formed UUID" "uuid" "uuid"
  else
    check "the default $role is a well-formed UUID" "uuid" "$value"
  fi
done

# --- the wallet pre-check must run BEFORE the lifecycle leg, or an unfunded
# --- canary 402s on the offer and it reads like a chat outage.
case "$PLAN_OUT" in
  *'/v1/jeeb/wallet'*) check "the plan reads the jeeber wallet" "present" "present" ;;
  *) check "the plan reads the jeeber wallet" "present" "MISSING" ;;
esac
WALLET_AT="$(printf '%s' "$PLAN_OUT" | grep -n '/v1/jeeb/wallet' | head -1 | cut -d: -f1)"
OFFER_AT="$(printf '%s' "$PLAN_OUT" | grep -n '/offers' | head -1 | cut -d: -f1)"
if [ -n "$WALLET_AT" ] && [ -n "$OFFER_AT" ] && [ "$WALLET_AT" -lt "$OFFER_AT" ]; then
  check "the wallet check precedes the offer" "before" "before"
else
  check "the wallet check precedes the offer" "before" "wallet@${WALLET_AT:-none} offer@${OFFER_AT:-none}"
fi

# --- the funding chain is planned by ensure-canary-accounts.sh, and its
# --- credential-bearing bodies are rendered as $VAR, never inline.
FUND_PLAN="$(JEEB_TOKEN_MINT_KEY=super-secret-value \
  bash "$SCRIPT_DIR/ensure-canary-accounts.sh" --base-url https://app.jeeb.fds-1.com --plan 2>&1)"
for hop in '/dev/partner/credentials' '/v1/partner/auth/login' '/wallet/credits' '/v1/partner/wallet/transfers/predict' '/v1/partner/wallet/transfers' '/v1/jeeb/wallet'; do
  case "$FUND_PLAN" in
    *"$hop"*) check "the funding plan covers $hop" "covered" "covered" ;;
    *) check "the funding plan covers $hop" "covered" "MISSING" ;;
  esac
done
case "$FUND_PLAN" in
  *'--data-binary $CANARY_PARTNER_LOGIN_BODY'*) check "the partner password is never inlined in a plan" "byvar" "byvar" ;;
  *) check "the partner password is never inlined in a plan" "byvar" "INLINED" ;;
esac
case "$FUND_PLAN" in
  *'"password"'*) check "no literal password field reaches the plan" "absent" "PRESENT" ;;
  *) check "no literal password field reaches the plan" "absent" "absent" ;;
esac
case "$FUND_PLAN" in
  *super-secret-value*) check "the funding plan never prints the mint key" "absent" "PRESENT" ;;
  *) check "the funding plan never prints the mint key" "absent" "absent" ;;
esac

# --- the scheduled workflow must FUND before it RUNS, or leg 6 402s on the
# --- offer guard every time and only a hand-run of the ensure script fixes it.
WORKFLOW="$SCRIPT_DIR/../../.github/workflows/jeeb-chat-push-canary.yml"
if [ -f "$WORKFLOW" ]; then
  ENSURE_AT="$(grep -n 'ensure-canary-accounts\.sh' "$WORKFLOW" | head -1 | cut -d: -f1)"
  EXECUTE_AT="$(grep -n -- '--execute' "$WORKFLOW" | head -1 | cut -d: -f1)"
  if [ -n "$ENSURE_AT" ]; then
    check "the workflow invokes ensure-canary-accounts.sh" "invoked" "invoked"
  else
    check "the workflow invokes ensure-canary-accounts.sh" "invoked" "MISSING"
  fi
  if [ -n "$ENSURE_AT" ] && [ -n "$EXECUTE_AT" ] && [ "$ENSURE_AT" -lt "$EXECUTE_AT" ]; then
    check "the workflow funds before it runs the canary" "before" "before"
  else
    check "the workflow funds before it runs the canary" "before" "ensure@${ENSURE_AT:-none} execute@${EXECUTE_AT:-none}"
  fi
  case "$(sed -n "${ENSURE_AT:-1}p" "$WORKFLOW")" in
    *--plan*) check "the workflow funds for real, not in plan mode" "execute" "PLAN" ;;
    *) check "the workflow funds for real, not in plan mode" "execute" "execute" ;;
  esac
  if grep -q 'GITHUB_STEP_SUMMARY' "$WORKFLOW"; then
    check "the funding step surfaces its READY line" "surfaced" "surfaced"
  else
    check "the funding step surfaces its READY line" "surfaced" "MISSING"
  fi
else
  check "the canary workflow is present" "found" "MISSING"
fi

# The ensure script must actually emit the line the workflow greps for.
case "$FUND_PLAN" in
  *'READY: client='*) check "the ensure script emits a READY summary line" "present" "present" ;;
  *) check "the ensure script emits a READY summary line" "present" "MISSING" ;;
esac

printf '\n%s case(s), %s failure(s)\n' "$CASES" "$FAILURES"
[ "$FAILURES" -eq 0 ] || exit 1
printf 'canary lib contract holds.\n'
