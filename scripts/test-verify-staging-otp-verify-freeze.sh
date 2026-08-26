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
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) output=$2; shift 2 ;;
    --dump-header) headers=$2; shift 2 ;;
    --write-out|--connect-timeout|--max-time|--proto|--request|--header)
      shift 2
      ;;
    --silent|--tlsv1.2) shift ;;
    https://*) url=$1; shift ;;
    *) exit 97 ;;
  esac
done

[ -n "$output" ] && [ -n "$headers" ] && [ -n "$url" ]
printf '%s\n' "$url" >> "$FREEZE_TEST_CALLS"
case "${FREEZE_TEST_SCENARIO:-success}" in
  transport) exit 28 ;;
  wrong-status) status=502 ;;
  *) status=503 ;;
esac

content_type=application/problem+json
cache_control=no-store
extra_header=''
body='{"type":"about:blank","title":"Service Unavailable","status":503,"detail":"The service is temporarily unavailable. Please try again."}'
case "${FREEZE_TEST_SCENARIO:-success}" in
  wrong-content-type) content_type=application/json ;;
  missing-cache) cache_control='' ;;
  retry-after) extra_header=$'Retry-After: 30\r\n' ;;
  extra-field) body='{"type":"about:blank","title":"Service Unavailable","status":503,"detail":"The service is temporarily unavailable. Please try again.","code":"frozen"}' ;;
esac
{
  printf 'HTTP/2 %s\r\n' "$status"
  printf 'Content-Type: %s\r\n' "$content_type"
  [ -z "$cache_control" ] || printf 'Cache-Control: %s\r\n' "$cache_control"
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

for rejected in wrong-status wrong-content-type missing-cache retry-after \
  extra-field newline-body transport; do
  run_case "$rejected" reject
done

set +e
PATH="$fake_bin:$PATH" FREEZE_TEST_SCENARIO=success \
  FREEZE_TEST_CALLS="$test_root/calls-invalid-nonce" \
  STAGING_OTP_FREEZE_NONCE='caller-data' bash "$verifier" >/dev/null 2>&1
invalid_nonce_status=$?
set -e
[ "$invalid_nonce_status" -ne 0 ]

echo 'staging OTP verify freeze tests: PASS (1 exact positive, 8 adversarial negatives)'
