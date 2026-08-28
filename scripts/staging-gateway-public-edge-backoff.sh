#!/usr/bin/env bash
set -euo pipefail

[ "$#" -ge 1 ] || exit 64
probe=$1
shift
[ -f "$probe" ] || exit 64
if [ "$#" -eq 0 ]; then
  probe_arguments=(devtool)
  phase=devtool-public-edge-read-only
elif [ "$#" -eq 6 ]; then
  case "$1" in
    posture) phase=recovery-public-posture ;;
    devtool-posture) phase=recovery-devtool-posture ;;
    *) exit 64 ;;
  esac
  probe_arguments=("$@")
else
  exit 64
fi

delay=1
for attempt in $(seq 1 8); do
  echo "staging phase=${phase} attempt=${attempt}/8 result=started (redacted)"
  if bash "$probe" "${probe_arguments[@]}" >/dev/null; then
    echo "staging phase=${phase} attempt=${attempt}/8 result=passed (redacted)"
    exit 0
  fi
  if [ "$attempt" -lt 8 ]; then
    echo "staging phase=${phase} attempt=${attempt}/8 result=retrying (redacted)" >&2
    sleep "$delay"
    if [ "$delay" -lt 8 ]; then
      delay=$((delay * 2))
      [ "$delay" -le 8 ] || delay=8
    fi
  fi
done

echo "staging phase=${phase} attempts=8 result=terminal-failure (redacted)" >&2
exit 1
