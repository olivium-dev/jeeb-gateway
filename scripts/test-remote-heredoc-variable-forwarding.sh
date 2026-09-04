#!/usr/bin/env bash
# Mutation proof for check-remote-heredoc-variable-forwarding.sh. A guard nobody has seen
# go red is folklore; run 33819016720 is what it costs when it stays green by accident.
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

workflow=.github/workflows/jeeb-staging-deploy.yml
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

run_guard() {
  JEEB_STAGING_WORKFLOW_UNDER_TEST=$1 \
    bash scripts/check-remote-heredoc-variable-forwarding.sh
}

expect_pass() {
  local label=$1 candidate=$2 output status=0
  output=$(run_guard "$candidate" 2>&1) || status=$?
  if [ "$status" -ne 0 ]; then
    echo "FAIL: $label was rejected: $output" >&2
    exit 1
  fi
}

expect_reject() {
  local label=$1 candidate=$2 needle=$3 output status=0
  output=$(run_guard "$candidate" 2>&1) || status=$?
  if [ "$status" -eq 0 ]; then
    echo "FAIL: $label was accepted" >&2
    exit 1
  fi
  case "$output" in
    *"$needle"*) ;;
    *) echo "FAIL: $label rejected without naming $needle: $output" >&2; exit 1 ;;
  esac
}

expect_pass 'the committed workflow' "$workflow"

# M1 — the exact regression of run 33819016720: the flag is expanded but not forwarded.
python3 - "$workflow" "$work/m1.yml" <<'PY'
import sys
from pathlib import Path
source = Path(sys.argv[1]).read_text()
mutant = source.replace("              chat_upstream_enabled \\\n", "", 1)
if mutant == source:
    raise SystemExit("FAIL: chat_upstream_enabled is no longer in the forwarding list")
Path(sys.argv[2]).write_text(mutant)
PY
expect_reject 'M1 chat_upstream_enabled dropped from the forwarding list' \
  "$work/m1.yml" chat_upstream_enabled

# M2 — the NEXT variable: a new expansion added without extending the list.
python3 - "$workflow" "$work/m2.yml" <<'PY'
import sys
from pathlib import Path
source = Path(sys.argv[1]).read_text()
anchor = '              add_env FeatureFlags__UseUpstream__Chat "$chat_upstream_enabled"\n'
if anchor not in source:
    raise SystemExit("FAIL: the resolved chat binding moved; re-anchor this mutant")
mutant = source.replace(
    anchor, anchor + '              add_env Canary__Next "$a_future_unforwarded_name"\n', 1)
Path(sys.argv[2]).write_text(mutant)
PY
expect_reject 'M2 a future unforwarded expansion' "$work/m2.yml" a_future_unforwarded_name

# M3 — a jq --arg name must NOT be mistaken for a shell expansion (the false-positive that
# would make operators disable this guard).
python3 - "$workflow" "$work/m3.yml" <<'PY'
import sys
from pathlib import Path
source = Path(sys.argv[1]).read_text()
anchor = '              add_env FeatureFlags__UseUpstream__Chat "$chat_upstream_enabled"\n'
mutant = source.replace(
    anchor,
    anchor + "              jq -cn --arg only_a_jq_arg x '{k:$only_a_jq_arg}' >/dev/null\n",
    1)
if mutant == source:
    raise SystemExit("FAIL: could not place the jq canary")
Path(sys.argv[2]).write_text(mutant)
PY
expect_pass 'M3 a jq program variable is not a shell expansion' "$work/m3.yml"

# M4 — the guard must notice if the serializer itself is reshaped away.
python3 - "$workflow" "$work/m4.yml" <<'PY'
import sys
from pathlib import Path
source = Path(sys.argv[1]).read_text()
mutant = source.replace("for variable in IMAGE", "for variable_renamed in IMAGE", 1)
if mutant == source:
    raise SystemExit("FAIL: the serializer anchor moved")
Path(sys.argv[2]).write_text(mutant)
PY
expect_reject 'M4 the forwarding serializer reshaped' "$work/m4.yml" serializer

echo "REMOTE heredoc forwarding guard: PASS (4 mutants)"
