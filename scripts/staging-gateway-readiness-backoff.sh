#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 2 ] || exit 64
published=$1
health=$2
[[ "$published" =~ ^[0-9]+$ ]]
[[ "$health" =~ ^/[A-Za-z0-9/_-]+$ ]]

readiness_delay=1
for attempt in $(seq 1 20); do
  if curl -fsS --max-time 5 \
    "http://127.0.0.1:${published}${health}" >/dev/null; then
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
exit 1
