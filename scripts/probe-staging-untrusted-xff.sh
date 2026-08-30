#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 4 ] || exit 64
timestamp=$1
nonce=$2
signature=$3
spoofed_remote_ip=$4
[[ "$timestamp" =~ ^[0-9]+$ ]]
[[ "$nonce" =~ ^[0-9a-f-]{36}$ ]]
[[ "$signature" =~ ^[0-9a-f]{64}$ ]]

response_headers=$(mktemp)
trap 'status=$?; rm -f -- "$response_headers"; exit "$status"' EXIT
status=$(curl --silent --show-error --max-time 20 \
  --output /dev/null --dump-header "$response_headers" \
  --write-out '%{http_code}' --request POST \
  --header "X-Forwarded-For: $spoofed_remote_ip" \
  --header "X-Jeeb-Staging-Probe-Timestamp: $timestamp" \
  --header "X-Jeeb-Staging-Probe-Nonce: $nonce" \
  --header "X-Jeeb-Staging-Probe-Signature: $signature" \
  http://127.0.0.1:10000/internal/ops/staging/realtime-probe-descriptor)
[ "$status" = 200 ]

observed=$(awk -v expected_name="x-jeeb-staging-observed-remote-ip" '
  {
    line=$0
    sub(/\r$/, "", line)
    name=line
    sub(/:.*/, "", name)
    if (tolower(name) == expected_name) {
      value=line
      sub(/^[^:]*:[[:space:]]*/, "", value)
      values[++matches]=value
    }
  }
  END {
    if (matches != 1 || values[1] == "") exit 1
    print values[1]
  }
' "$response_headers")

python3 - "$observed" "$spoofed_remote_ip" <<'PY'
import ipaddress
import sys

observed = ipaddress.ip_address(sys.argv[1])
spoofed = ipaddress.ip_address(sys.argv[2])
if observed == spoofed:
    raise SystemExit(1)
PY
