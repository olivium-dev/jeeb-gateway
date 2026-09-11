#!/usr/bin/env bash
set -euo pipefail

[ "$#" -ge 2 ] && [ "$#" -le 3 ] || exit 64
published=$1
health=$2
delivery_probe=${3:-none}
case "$delivery_probe" in none|credential|catalog) ;; *) exit 64 ;; esac
[[ "$published" =~ ^[0-9]+$ ]]
[[ "$health" =~ ^/[A-Za-z0-9/_-]+$ ]]

probe_readiness() {
  local readiness
  readiness=$(curl -fsS --max-time 5 \
    "http://127.0.0.1:${published}${health}") || return 1
  [ "$delivery_probe" != none ] || return 0
  # Degraded deliberately remains HTTP 200 for staged-off services. Delivery is
  # enabled by this deploy, so its credential and owner rows must be Healthy.
  jq -e '
    [.checks[]? | select(.name == "credential-delivery-service-token")]
      as $credential
    | [.checks[]? | select(.name == "delivery-service")] as $owner
    | ($credential | length) == 1 and $credential[0].status == "Healthy"
      and ($owner | length) == 1 and $owner[0].status == "Healthy"
  ' <<< "$readiness" >/dev/null || return 1
  [ "$delivery_probe" = catalog ] || return 0
  # /health on delivery bypasses service authentication. With Delivery=true,
  # /tiers calls the authenticated owner directly on every request, proving the
  # token can be loaded and is accepted (including a wrong-but-readable secret).
  curl -fsS --max-time 5 "http://127.0.0.1:${published}/tiers" \
    | jq -e 'type == "object" and (.items | type) == "array"' >/dev/null
}

readiness_delay=1
for attempt in $(seq 1 20); do
  if probe_readiness; then
    exit 0
  fi
  if [ "$attempt" -lt 20 ]; then
    sleep "$readiness_delay"
    if [ "$readiness_delay" -lt 8 ]; then
      readiness_delay=$((readiness_delay * 2))
      [ "$readiness_delay" -le 8 ] || readiness_delay=8
    fi
  fi
done
echo 'Gateway readiness or authenticated delivery catalog verification failed.' >&2
exit 1
