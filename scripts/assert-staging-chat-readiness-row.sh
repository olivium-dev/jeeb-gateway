#!/usr/bin/env bash
# Asserts the chat-upstream-readiness row on /health/ready (JSON on stdin) matches the
# chat state this deploy declared. Degraded is HTTP 200, so curl alone cannot see it.
set -euo pipefail

expected=${1:?expected chat state (true|false) is required}
case "$expected" in true|false) ;; *)
  echo "::error::expected chat state must be true or false" >&2
  exit 64
esac

readiness=$(cat)
row=$(jq -c '[.checks[]? | select(.name == "chat-upstream-readiness")]' <<<"$readiness") || {
  echo '::error::/health/ready payload is not parseable JSON' >&2
  exit 1
}
[ "$(jq -r 'length' <<<"$row")" = 1 ] || {
  echo '::error::/health/ready carries no single chat-upstream-readiness row' >&2
  exit 1
}

status=$(jq -r '.[0].status' <<<"$row")
description=$(jq -r '.[0].description // ""' <<<"$row")

if [ "$expected" = true ]; then
  [ "$status" = Healthy ] || {
    echo "::error::chat was deployed ON but chat-upstream-readiness is $status: $description" >&2
    exit 1
  }
  echo "chat-upstream-readiness=Healthy as declared (chat on)"
else
  case "$status:$description" in
    Degraded:*"disabled by flag"*)
      echo "chat-upstream-readiness=Degraded 'disabled by flag' as declared (chat off)" ;;
    *)
      echo "::error::chat was deployed OFF but chat-upstream-readiness is $status: $description" >&2
      exit 1 ;;
  esac
fi
