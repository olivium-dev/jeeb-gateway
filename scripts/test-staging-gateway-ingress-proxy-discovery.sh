#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
script="$repository_root/scripts/staging-gateway-ingress-proxy-discovery.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir -p "$fake_bin"

cat > "$fake_bin/curl" <<'FAKE_CURL'
#!/usr/bin/env bash
set -euo pipefail
headers=''
output=''
url=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --dump-header) headers=$2; shift 2 ;;
    --output) output=$2; shift 2 ;;
    --header)
      case "$2" in
        'X-Forwarded-For:'*) exit 90 ;;
        'X-Jeeb-Staging-Probe-Timestamp: 1700000000'|\
        'X-Jeeb-Staging-Probe-Nonce: 12345678-1234-1234-1234-123456789abc'|\
        'X-Jeeb-Staging-Probe-Signature: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') ;;
        *) exit 91 ;;
      esac
      shift 2
      ;;
    --write-out|--max-time|--request) shift 2 ;;
    --silent|--show-error) shift ;;
    http://127.0.0.1:10000/internal/ops/staging/realtime-probe-descriptor) url=$1; shift ;;
    *) exit 92 ;;
  esac
done
[ -n "$headers" ] && [ "$output" = /dev/null ] && [ -n "$url" ]
[ "${PROXY_TEST_SCENARIO:-success}" != transport ] || exit 28
case "${PROXY_TEST_SCENARIO:-success}" in
  wrong-status) status=403 ;;
  *) status=200 ;;
esac
case "${PROXY_TEST_SCENARIO:-success}" in
  missing-header) printf 'HTTP/1.1 %s\r\n\r\n' "$status" > "$headers" ;;
  duplicate-header) printf 'HTTP/1.1 %s\r\nX-Jeeb-Staging-Observed-Remote-IP: 10.0.0.2\r\nX-Jeeb-Staging-Observed-Remote-IP: 10.0.0.3\r\n\r\n' "$status" > "$headers" ;;
  out-of-subnet) printf 'HTTP/1.1 %s\r\nX-Jeeb-Staging-Observed-Remote-IP: 172.18.0.1\r\n\r\n' "$status" > "$headers" ;;
  *) printf 'HTTP/1.1 %s\r\nX-Jeeb-Staging-Observed-Remote-IP: 10.0.0.2\r\n\r\n' "$status" > "$headers" ;;
esac
printf '%s' "$status"
FAKE_CURL

cat > "$fake_bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ] && [ "$1" = network ] && [ "$2" = inspect ] && [ "$3" = ingress ]
[ "${PROXY_TEST_SCENARIO:-success}" != inspect-failure ] || exit 1
printf '%s\n' '[{"Driver":"overlay","Scope":"swarm","Ingress":true,"IPAM":{"Config":[{"Subnet":"10.0.0.0/24","Gateway":"10.0.0.1"}]}}]'
FAKE_DOCKER
chmod +x "$fake_bin/curl" "$fake_bin/docker"

run_case() {
  local scenario=$1 expected=$2 output status
  set +e
  output=$(PATH="$fake_bin:$PATH" PROXY_TEST_SCENARIO="$scenario" \
    bash "$script" 1700000000 12345678-1234-1234-1234-123456789abc \
      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 2>/dev/null)
  status=$?
  set -e
  if [ "$expected" = pass ]; then
    [ "$status" -eq 0 ] && [ "$output" = 10.0.0.2 ]
  else
    [ "$status" -ne 0 ] && [ -z "$output" ]
  fi
}

run_case success pass
for scenario in wrong-status missing-header duplicate-header out-of-subnet transport inspect-failure; do
  run_case "$scenario" reject
done

echo 'staging ingress proxy discovery tests: PASS (1 exact positive, 6 fail-closed negatives)'
