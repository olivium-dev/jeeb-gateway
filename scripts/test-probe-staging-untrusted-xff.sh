#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/probe-staging-untrusted-xff.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir -p "$fake_bin"

cat > "$fake_bin/curl" <<'FAKE_CURL'
#!/usr/bin/env bash
set -euo pipefail
headers=''
output=''
status_format=''
method=''
timeout=''
url=''
xff_count=0
timestamp_count=0
nonce_count=0
signature_count=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --dump-header) headers=$2; shift 2 ;;
    --output) output=$2; shift 2 ;;
    --write-out) status_format=$2; shift 2 ;;
    --request) method=$2; shift 2 ;;
    --max-time) timeout=$2; shift 2 ;;
    --header)
      case "$2" in
        'X-Forwarded-For: 198.51.100.42') xff_count=$((xff_count + 1)) ;;
        'X-Jeeb-Staging-Probe-Timestamp: 1700000000') timestamp_count=$((timestamp_count + 1)) ;;
        'X-Jeeb-Staging-Probe-Nonce: 12345678-1234-1234-1234-123456789abc') nonce_count=$((nonce_count + 1)) ;;
        'X-Jeeb-Staging-Probe-Signature: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') signature_count=$((signature_count + 1)) ;;
        *) exit 93 ;;
      esac
      shift 2
      ;;
    --silent|--show-error) shift ;;
    http://127.0.0.1:10000/internal/ops/staging/realtime-probe-descriptor) url=$1; shift ;;
    *) exit 92 ;;
  esac
done
[ "$output" = /dev/null ]
[ "$status_format" = '%{http_code}' ]
[ "$method" = POST ]
[ "$timeout" = 20 ]
[ -n "$headers" ] && [ -n "$url" ]
[ "$xff_count" -eq 1 ]
[ "$timestamp_count" -eq 1 ]
[ "$nonce_count" -eq 1 ]
[ "$signature_count" -eq 1 ]
[ "${XFF_TEST_SCENARIO:-success}" != transport ] || exit 28
case "${XFF_TEST_SCENARIO:-success}" in
  wrong-status) status=403 ;;
  *) status=200 ;;
esac
case "${XFF_TEST_SCENARIO:-success}" in
  missing-header) value='' ;;
  duplicate-header) value=$'X-Jeeb-Staging-Observed-Remote-IP: 10.0.0.2\r\nX-Jeeb-Staging-Observed-Remote-IP: 10.0.0.3' ;;
  promoted) value='X-Jeeb-Staging-Observed-Remote-IP: 198.51.100.42' ;;
  malformed) value='X-Jeeb-Staging-Observed-Remote-IP: 10.0.0.2:garbage' ;;
  invalid-ip) value='X-Jeeb-Staging-Observed-Remote-IP: not-an-ip' ;;
  *) value='X-Jeeb-Staging-Observed-Remote-IP: 10.0.0.2' ;;
esac
printf 'HTTP/1.1 %s\r\n%s\r\n\r\n' "$status" "$value" > "$headers"
printf '%s' "$status"
FAKE_CURL
chmod +x "$fake_bin/curl"

run_case() {
  local scenario=$1 expected=$2 status
  set +e
  PATH="$fake_bin:$PATH" XFF_TEST_SCENARIO="$scenario" \
    bash "$subject" 1700000000 12345678-1234-1234-1234-123456789abc \
      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
      198.51.100.42 >/dev/null 2>&1
  status=$?
  set -e
  if [ "$expected" = pass ]; then
    [ "$status" -eq 0 ]
  else
    [ "$status" -ne 0 ]
  fi
}

run_case success pass
for scenario in wrong-status missing-header duplicate-header promoted malformed invalid-ip transport; do
  run_case "$scenario" reject
done

echo 'staging untrusted-XFF probe tests: PASS (1 exact positive, 7 fail-closed negatives)'
