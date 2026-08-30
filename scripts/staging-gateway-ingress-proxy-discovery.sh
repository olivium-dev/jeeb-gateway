#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 3 ] || exit 64
timestamp=$1
nonce=$2
signature=$3
[[ "$timestamp" =~ ^[0-9]+$ ]]
[[ "$nonce" =~ ^[0-9a-f-]{36}$ ]]
[[ "$signature" =~ ^[0-9a-f]{64}$ ]]

private_root=$(mktemp -d)
chmod 700 "$private_root"
headers="$private_root/headers"
ingress_document="$private_root/ingress.json"
trap 'status=$?; rm -rf -- "$private_root"; exit "$status"' EXIT

status=$(curl --silent --show-error --max-time 20 \
  --output /dev/null --dump-header "$headers" \
  --write-out '%{http_code}' --request POST \
  --header "X-Jeeb-Staging-Probe-Timestamp: $timestamp" \
  --header "X-Jeeb-Staging-Probe-Nonce: $nonce" \
  --header "X-Jeeb-Staging-Probe-Signature: $signature" \
  http://127.0.0.1:10000/internal/ops/staging/realtime-probe-descriptor)
[ "$status" = 200 ] || exit 1

observed=$(awk -F': *' '
  tolower($1) == "x-jeeb-staging-observed-remote-ip" {
    gsub(/\r$/, "", $2)
    values[++count] = $2
  }
  END {
    if (count != 1 || values[1] == "") exit 1
    print values[1]
  }
' "$headers")

docker network inspect ingress > "$ingress_document"
python3 - "$observed" "$ingress_document" <<'PY'
import ipaddress
import json
import sys

observed = ipaddress.ip_address(sys.argv[1])
with open(sys.argv[2], encoding="utf-8") as stream:
    document = json.load(stream)

if (
    observed.version != 4
    or observed.is_unspecified
    or observed.is_loopback
    or observed.is_multicast
    or observed.is_link_local
    or not isinstance(document, list)
    or len(document) != 1
):
    raise SystemExit(1)

network = document[0]
if (
    network.get("Driver") != "overlay"
    or network.get("Scope") != "swarm"
    or network.get("Ingress") is not True
):
    raise SystemExit(1)

subnets = []
for row in network.get("IPAM", {}).get("Config", []):
    value = row.get("Subnet") if isinstance(row, dict) else None
    if not isinstance(value, str):
        continue
    parsed = ipaddress.ip_network(value, strict=False)
    if parsed.version == 4:
        subnets.append(parsed)

matches = [item for item in subnets if observed in item]
if len(matches) != 1:
    raise SystemExit(1)
selected = matches[0]
if observed in (selected.network_address, selected.broadcast_address):
    raise SystemExit(1)

print(observed)
PY
