#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
verifier="$repository_root/scripts/verify-staging-otp-verify-freeze.sh"
test_root=$(mktemp -d)
trap 'status=$?; rm -rf -- "$test_root"; exit "$status"' EXIT
fake_bin="$test_root/bin"
mkdir -p "$fake_bin"

cat > "$fake_bin/curl" <<'FAKE_CURL'
#!/usr/bin/env bash
set -euo pipefail

output=''
headers=''
url=''
accept_seen=false
user_agent_seen=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) output=$2; shift 2 ;;
    --dump-header) headers=$2; shift 2 ;;
    --header)
      case "$2" in
        'Accept: application/problem+json') accept_seen=true ;;
        'User-Agent: Jeeb-Staging-Deploy/1.0') user_agent_seen=true ;;
        *) exit 96 ;;
      esac
      shift 2
      ;;
    --write-out|--connect-timeout|--max-time|--proto|--request)
      shift 2
      ;;
    --silent|--tlsv1.2) shift ;;
    https://*) url=$1; shift ;;
    *) exit 97 ;;
  esac
done

[ -n "$output" ] && [ -n "$headers" ] && [ -n "$url" ]
[ "$accept_seen" = true ] && [ "$user_agent_seen" = true ]
printf '%s\n' "$url" >> "$FREEZE_TEST_CALLS"
case "${FREEZE_TEST_SCENARIO:-success}" in
  transport) exit 28 ;;
  wrong-status) status=503 ;;
  cloudflare-block) status=403 ;;
  *) status=401 ;;
esac

content_type='application/problem+json; charset=utf-8'
extra_header=''
request_path=${url#https://app.jeeb.fds-1.com}
request_path=${request_path%%\?*}
body=$(printf '{"type":"https://problems.jeeb.lb/auth/invalid_otp","title":"Invalid code","status":401,"detail":"The OTP code is missing or empty.","instance":"%s"}' "$request_path")
case "${FREEZE_TEST_SCENARIO:-success}" in
  wrong-content-type) content_type=application/json ;;
  retry-after) extra_header=$'Retry-After: 30\r\n' ;;
  set-cookie) extra_header=$'Set-Cookie: session=forbidden\r\n' ;;
  authorization) extra_header=$'Authorization: Bearer forbidden\r\n' ;;
  wrong-instance) body='{"type":"https://problems.jeeb.lb/auth/invalid_otp","title":"Invalid code","status":401,"detail":"The OTP code is missing or empty.","instance":"/wrong"}' ;;
  extra-field) body=$(printf '{"type":"https://problems.jeeb.lb/auth/invalid_otp","title":"Invalid code","status":401,"detail":"The OTP code is missing or empty.","instance":"%s","code":"unexpected"}' "$request_path") ;;
  cloudflare-block) body='{"type":"https://developers.cloudflare.com/support/troubleshooting/http-status-codes/cloudflare-1xxx-errors/error-1010/","title":"Error 1010: Access denied","status":403,"error_code":1010}' ;;
esac
{
  printf 'HTTP/2 %s\r\n' "$status"
  printf 'Content-Type: %s\r\n' "$content_type"
  printf '%s' "$extra_header"
  printf '\r\n'
} > "$headers"
printf '%s' "$body" > "$output"
[ "${FREEZE_TEST_SCENARIO:-success}" != newline-body ] || printf '\n' >> "$output"
printf '%s' "$status"
FAKE_CURL
chmod +x "$fake_bin/curl"

run_case() {
  local scenario=$1 expected=$2
  local calls="$test_root/calls-${scenario}"
  : > "$calls"
  set +e
  output=$(PATH="$fake_bin:$PATH" \
    FREEZE_TEST_SCENARIO="$scenario" \
    FREEZE_TEST_CALLS="$calls" \
    STAGING_OTP_FREEZE_NONCE=0123456789abcdef0123456789abcdef \
    bash "$verifier" 2>&1)
  status=$?
  set -e
  if [ "$expected" = pass ]; then
    [ "$status" -eq 0 ] || {
      printf 'expected freeze verifier success for %s, got %s: %s\n' \
        "$scenario" "$status" "$output" >&2
      exit 1
    }
  else
    [ "$status" -ne 0 ] || {
      printf 'unsafe freeze response passed: %s\n' "$scenario" >&2
      exit 1
    }
  fi
}

run_case success pass
success_calls="$test_root/calls-success"
[ "$(wc -l < "$success_calls" | tr -d ' ')" -eq 4 ]
for exact_url in \
  'https://app.jeeb.fds-1.com/v1/auth/otp/verify?cutover_nonce=0123456789abcdef0123456789abcdef' \
  'https://app.jeeb.fds-1.com/v1/auth/otp/verify/?cutover_nonce=0123456789abcdef0123456789abcdef' \
  'https://app.jeeb.fds-1.com/auth/otp/verify?cutover_nonce=0123456789abcdef0123456789abcdef' \
  'https://app.jeeb.fds-1.com/auth/otp/verify/?cutover_nonce=0123456789abcdef0123456789abcdef'; do
  grep -Fxq "$exact_url" "$success_calls"
done
if grep -Fq '/otp/request' "$success_calls"; then
  echo 'freeze verifier touched the forbidden OTP request route' >&2
  exit 1
fi

for rejected in wrong-status wrong-content-type retry-after set-cookie authorization \
  wrong-instance extra-field newline-body cloudflare-block transport; do
  run_case "$rejected" reject
done

set +e
PATH="$fake_bin:$PATH" FREEZE_TEST_SCENARIO=success \
  FREEZE_TEST_CALLS="$test_root/calls-invalid-nonce" \
  STAGING_OTP_FREEZE_NONCE='caller-data' bash "$verifier" >/dev/null 2>&1
invalid_nonce_status=$?
set -e
[ "$invalid_nonce_status" -ne 0 ]

echo 'staging OTP verify fail-closed tests: PASS (1 exact positive, 10 adversarial negatives)'
