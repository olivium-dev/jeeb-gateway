#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || exit 64
probe=$1
[ -f "$probe" ] || exit 64

delay=1
for attempt in $(seq 1 8); do
  echo "staging phase=devtool-public-edge-read-only attempt=${attempt}/8 result=started (redacted)"
  if bash "$probe" devtool >/dev/null; then
    echo "staging phase=devtool-public-edge-read-only attempt=${attempt}/8 result=passed (redacted)"
    exit 0
  fi
  if [ "$attempt" -lt 8 ]; then
    echo "staging phase=devtool-public-edge-read-only attempt=${attempt}/8 result=retrying (redacted)" >&2
    sleep "$delay"
    if [ "$delay" -lt 8 ]; then
      delay=$((delay * 2))
      [ "$delay" -le 8 ] || delay=8
    fi
  fi
done

echo 'staging phase=devtool-public-edge-read-only attempts=8 result=terminal-failure (redacted)' >&2
exit 1
