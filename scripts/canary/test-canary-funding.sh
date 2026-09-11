#!/usr/bin/env bash
# Offline execution of the funding script. curl is replaced for every request.
set -euo pipefail
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir "$fixture/bin"
cat > "$fixture/bin/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
method= out= body= url=
while [ "$#" -gt 0 ]; do
  case "$1" in
    -X) method=$2; shift 2 ;;
    -o) out=$2; shift 2 ;;
    --data-binary) body=$2; shift 2 ;;
    -m|-H|-w) shift 2 ;;
    -sS) shift ;;
    https://fixture.invalid/*) url=$1; shift ;;
    *) exit 97 ;;
  esac
done
[ -n "$url" ] && [ -n "$out" ] || exit 97
response='{}'
code=200
case "$method $url" in
  'POST https://fixture.invalid/auth/tokens') response='{"accessToken":"fixture-token"}' ;;
  'GET https://fixture.invalid/v1/jeeb/wallet')
    balance=${FIXTURE_INITIAL_BALANCE:-0}
    currency=${FIXTURE_CURRENCY:-USD}
    if [ -f "$FUNDING_FIXTURE/ensured" ]; then
      currency=${FIXTURE_PROVISIONED_CURRENCY:-$currency}
    fi
    if [ -f "$FUNDING_FIXTURE/transferred" ]; then
      balance=${FIXTURE_FINAL_BALANCE:-40}
      currency=${FIXTURE_FINAL_CURRENCY:-$currency}
    fi
    response=$(jq -nc --argjson balance "$balance" --arg currency "$currency" \
      '{availableBalance:$balance,currency:($currency | if . == "null" then null else . end)}') ;;
  'PUT https://fixture.invalid/dev/wallets/jeeber/'*)
    touch "$FUNDING_FIXTURE/ensured"; code=204 ;;
  'POST https://fixture.invalid/dev/partner/credentials') code=204 ;;
  'POST https://fixture.invalid/v1/partner/auth/login')
    response='{"accessToken":"fixture-partner-token","partnerId":"ca9a4100-0000-4000-8000-000000000003"}' ;;
  'POST https://fixture.invalid/v1/admin/partners/'*'/wallet/credits')
    jq -e '.idempotencyKey | length <= 128' <<< "$body" >/dev/null
    jq -er '.idempotencyKey' <<< "$body" >> "$FUNDING_FIXTURE/credits"
    response='{"transactionId":"old-or-new-credit","status":"executed"}' ;;
  'POST https://fixture.invalid/v1/partner/wallet/transfers/predict') response='{"otpRequired":false}' ;;
  'POST https://fixture.invalid/v1/partner/wallet/transfers')
    jq -e '.idempotencyKey | length <= 128' <<< "$body" >/dev/null
    jq -er '.idempotencyKey' <<< "$body" >> "$FUNDING_FIXTURE/topups"
    touch "$FUNDING_FIXTURE/transferred"
    response='{"transactionId":"old-or-new-transfer","status":"executed"}' ;;
  'DELETE https://fixture.invalid/dev/partner/credentials/'*) code=204 ;;
  'GET https://fixture.invalid/v1/jeebers/me/availability'|'GET https://fixture.invalid/tiers') ;;
  'PUT https://fixture.invalid/api/PushNotification/register') code=201 ;;
  *) exit 97 ;;
esac
printf '%s' "$response" > "$out"
printf '%s' "$code"
CURL
chmod +x "$fixture/bin/curl"
export PATH="$fixture/bin:$PATH" JEEB_TOKEN_MINT_KEY=fixture-mint-key
export JEEB_CANARY_FUNDING_OPERATION_ID='' GITHUB_STEP_SUMMARY='' GITHUB_ACTIONS=''
export JEEB_CANARY_PARTNER_PASSWORD=fixture-password

run_funding() {
  local name=$1 expected=$2
  shift 2
  mkdir "$fixture/$name"
  local result=0
  FUNDING_FIXTURE="$fixture/$name" "$@" bash "$script_dir/ensure-canary-accounts.sh" \
    --base-url https://fixture.invalid > "$fixture/$name/output" 2>&1 || result=$?
  [ "$result" -eq "$expected" ] || { cat "$fixture/$name/output"; exit 1; }
}

# A 200 executed receipt with no balance movement remains a hard failure.
run_funding stale 1 env GITHUB_RUN_ID=12345 GITHUB_RUN_ATTEMPT=1 FIXTURE_FINAL_BALANCE=0
run_funding retry 1 env GITHUB_RUN_ID=12345 GITHUB_RUN_ATTEMPT=2 FIXTURE_FINAL_BALANCE=0
cmp "$fixture/stale/credits" "$fixture/retry/credits"
cmp "$fixture/stale/topups" "$fixture/retry/topups"
run_funding newrun 0 env GITHUB_RUN_ID=12346
if cmp -s "$fixture/stale/credits" "$fixture/newrun/credits"; then exit 1; fi
if cmp -s "$fixture/stale/topups" "$fixture/newrun/topups"; then exit 1; fi
[ "$(wc -l < "$fixture/newrun/credits" | tr -d ' ')" -eq 1 ]
[ "$(wc -l < "$fixture/newrun/topups" | tr -d ' ')" -eq 1 ]
run_funding currency 0 env GITHUB_RUN_ID=12345 FIXTURE_CURRENCY=CREDIT
if cmp -s "$fixture/stale/credits" "$fixture/currency/credits"; then exit 1; fi
if cmp -s "$fixture/stale/topups" "$fixture/currency/topups"; then exit 1; fi

# Funded wallets skip all money writes, including a manual call without an ID.
run_funding funded 0 env GITHUB_RUN_ID= FIXTURE_INITIAL_BALANCE=40
[ ! -e "$fixture/funded/credits" ] && [ ! -e "$fixture/funded/topups" ]
run_funding manualmissing 1 env GITHUB_RUN_ID=
[ ! -e "$fixture/manualmissing/credits" ] && [ ! -e "$fixture/manualmissing/topups" ]
run_funding manual 0 env GITHUB_RUN_ID= JEEB_CANARY_FUNDING_OPERATION_ID=manual-recovery
run_funding manualretry 0 env GITHUB_RUN_ID= JEEB_CANARY_FUNDING_OPERATION_ID=manual-recovery
cmp "$fixture/manual/credits" "$fixture/manualretry/credits"
cmp "$fixture/manual/topups" "$fixture/manualretry/topups"
run_funding maxkey 0 env GITHUB_RUN_ID= JEEB_CANARY_FUNDING_OPERATION_ID=abcdefghijklmnopqrstuvwx FIXTURE_CURRENCY=ABCDEFGHIJKL
run_funding longid 1 env GITHUB_RUN_ID= JEEB_CANARY_FUNDING_OPERATION_ID=abcdefghijklmnopqrstuvwxy
[ ! -e "$fixture/longid/credits" ] && [ ! -e "$fixture/longid/topups" ]

# A new holder can acquire currency metadata at zero-balance provisioning.
run_funding provision 0 env GITHUB_RUN_ID=12347 FIXTURE_CURRENCY=null FIXTURE_PROVISIONED_CURRENCY=USD
run_funding unknown 1 env GITHUB_RUN_ID=12347 FIXTURE_CURRENCY=null
[ ! -e "$fixture/unknown/credits" ] && [ ! -e "$fixture/unknown/topups" ]
run_funding changed 1 env GITHUB_RUN_ID=12347 FIXTURE_FINAL_CURRENCY=CREDIT
echo 'Funding execution fixtures passed: retry identity, new run/currency, balance and currency gates.'
