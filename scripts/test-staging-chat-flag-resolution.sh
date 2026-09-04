#!/usr/bin/env bash
# Executes the deploy workflow's own chat-activation resolver (extracted, not
# reimplemented) against a stubbed ssh so the ratchet cannot come back silently.
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

python3 - "$work/resolver.sh" <<'PY'
import sys
from pathlib import Path

workflow = Path(".github/workflows/jeeb-staging-deploy.yml").read_text()
start = workflow.index('          chat_upstream_declared="${JEEB_STAGING_CHAT_ENABLED:-}"')
end = workflow.index('\n', workflow.index('(from $chat_upstream_source)"', start))
block = "\n".join(line[10:] for line in workflow[start:end].splitlines())
Path(sys.argv[1]).write_text("set -euo pipefail\nservice=jeeb-staging-jeeb-gateway\n" + block + "\n")
PY

mkdir -p "$work/bin"
cat > "$work/bin/ssh" <<'STUB'
#!/usr/bin/env bash
cat >/dev/null
printf '%s' "${STUB_PERSISTED_CHAT_FLAG-}"
[ "${STUB_SSH_EXIT:-0}" = 0 ] || exit "$STUB_SSH_EXIT"
STUB
chmod +x "$work/bin/ssh"

run_resolver() {
  env PATH="$work/bin:$PATH" bash "$work/resolver.sh"
}

expect() {
  local label=$1 expected=$2
  shift 2
  local output status=0
  output=$(run_resolver 2>&1) || status=$?
  if [ "$status" -ne 0 ] || [ "$output" != "$expected" ]; then
    echo "FAIL: $label -> status=$status output='"$output"'" >&2
    exit 1
  fi
}

STUB_PERSISTED_CHAT_FLAG='' JEEB_STAGING_CHAT_ENABLED='' \
  expect 'no declaration, no incumbent -> on, with a distinct warning' \
  '::warning::no persisted chat state on incumbent; defaulting true
Chat upstream for this deploy: true (from default (no persisted chat state on incumbent))'

STUB_PERSISTED_CHAT_FLAG='true' JEEB_STAGING_CHAT_ENABLED='' \
  expect 'activated incumbent carries forward' \
  'Chat upstream for this deploy: true (from persisted incumbent service state)'

STUB_PERSISTED_CHAT_FLAG='false' JEEB_STAGING_CHAT_ENABLED='' \
  expect 'deactivated incumbent carries forward' \
  'Chat upstream for this deploy: false (from persisted incumbent service state)'

STUB_PERSISTED_CHAT_FLAG='true' JEEB_STAGING_CHAT_ENABLED='false' \
  expect 'declaration overrides an activated incumbent' \
  'Chat upstream for this deploy: false (from declared vars.JEEB_STAGING_CHAT_ENABLED)'

STUB_PERSISTED_CHAT_FLAG='false' JEEB_STAGING_CHAT_ENABLED='true' \
  expect 'declaration overrides a deactivated incumbent' \
  'Chat upstream for this deploy: true (from declared vars.JEEB_STAGING_CHAT_ENABLED)'

STUB_PERSISTED_CHAT_FLAG='' STUB_SSH_EXIT=255 JEEB_STAGING_CHAT_ENABLED='' \
  expect 'unreadable incumbent fails open, and says so distinctly' \
  '::warning::could not read the incumbent chat state; defaulting true
Chat upstream for this deploy: true (from default (incumbent unreadable))'

status=0
env PATH="$work/bin:$PATH" JEEB_STAGING_CHAT_ENABLED='maybe' \
  bash "$work/resolver.sh" >/dev/null 2>&1 || status=$?
[ "$status" = 64 ] || {
  echo "FAIL: a malformed declaration must reject the deploy (got $status)" >&2
  exit 1
}

cat > "$work/bin/docker" <<'STUB'
#!/usr/bin/env bash
[ "${STUB_DOCKER_EXIT:-0}" = 0 ] || exit "$STUB_DOCKER_EXIT"
printf '%s\n' ${STUB_SERVICE_ENV-}
STUB
chmod +x "$work/bin/docker"

read_flag() {
  env PATH="$work/bin:$PATH" bash scripts/read-staging-chat-flag.sh jeeb-staging-jeeb-gateway
}

expect_flag() {
  local label=$1 expected=$2
  local actual status=0
  actual=$(read_flag) || status=$?
  if [ "$status" -ne 0 ] || [ "$actual" != "$expected" ]; then
    echo "FAIL: $label -> status=$status value='"$actual"'" >&2
    exit 1
  fi
}

STUB_SERVICE_ENV='ASPNETCORE_ENVIRONMENT=Staging FeatureFlags__UseUpstream__Chat=true' \
  expect_flag 'reads an activated incumbent' true
STUB_SERVICE_ENV='FeatureFlags__UseUpstream__Chat=false SuperLogin__OpenMode=true' \
  expect_flag 'reads a deactivated incumbent' false
STUB_SERVICE_ENV='FeatureFlags__UseUpstream__ChatLegacy=true' \
  expect_flag 'ignores a prefix-shaped neighbour key' ''
STUB_SERVICE_ENV='FeatureFlags__UseUpstream__Chat=yes' \
  expect_flag 'ignores a non-boolean value' ''
STUB_DOCKER_EXIT=1 expect_flag 'absent service yields no value' ''

row() {
  printf '{"status":"%s","checks":[{"name":"chat-upstream-readiness","status":"%s","description":"%s"}]}' \
    "$1" "$1" "$2"
}

assert_row() {
  local label=$1 expected=$2 json=$3 want=$4 status=0
  printf '%s' "$json" \
    | bash scripts/assert-staging-chat-readiness-row.sh "$expected" >/dev/null 2>&1 || status=$?
  if [ "$status" != "$want" ]; then
    echo "FAIL: $label -> exit $status, wanted $want" >&2
    exit 1
  fi
}

assert_row 'chat on + Healthy row passes' true \
  "$(row Healthy 'chat-service api/Health/firebase passed (Firestore reachable)')" 0
# Healthy is not proof: the 204 branch is Healthy but says UNVERIFIED. The deploy gate
# must demand the HTTP-200 branch, which is the only real Firestore round-trip.
assert_row 'chat on + legacy 204 (Healthy but UNVERIFIED) is rejected' true \
  "$(row Healthy 'chat-service api/Health/firebase returned 204; Firestore round-trip UNVERIFIED (legacy 204)')" 1
assert_row 'chat off tolerates an UNVERIFIED description it never reaches' false \
  "$(row Degraded 'chat disabled by flag (FeatureFlags:UseUpstream:Chat=false)')" 0
assert_row 'chat on but silently disabled is rejected' true \
  "$(row Degraded 'chat disabled by flag (FeatureFlags:UseUpstream:Chat=false)')" 1
assert_row 'chat on but Firestore UNVERIFIED is rejected' true \
  "$(row Degraded 'chat-service has no api/Health/firebase route (404) on this build')" 1
assert_row 'chat off + disabled-by-flag row passes' false \
  "$(row Degraded 'chat disabled by flag (FeatureFlags:UseUpstream:Chat=false)')" 0
assert_row 'chat off but a live chat row is rejected' false \
  "$(row Healthy 'chat-service api/Health/firebase passed (Firestore reachable)')" 1
assert_row 'a missing chat row is rejected' true \
  '{"status":"Healthy","checks":[{"name":"self","status":"Healthy"}]}' 1
assert_row 'an unparseable payload is rejected' true '<html>503</html>' 1
assert_row 'a malformed expectation is rejected' maybe "$(row Healthy ok)" 64

echo "Staging chat activation resolver and readiness-row assertion: PASS"
