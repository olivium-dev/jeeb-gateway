#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

failed=0

report_tracked_matches() {
  local label="$1"
  local pattern="$2"
  local matches
  matches="$(git grep -n -I -E "$pattern" -- . \
    ':!tests/**' \
    ':!scripts/check-forward-only-authorities.sh' \
    ':!.github/workflows/forward-only-authority-audit.yml' || true)"
  if [[ -n "$matches" ]]; then
    echo "FAIL: $label"
    echo "$matches"
    failed=1
  fi
}

# Repo-wide negative guard: deployment rollback primitives and executable schema downgrade
# commands may not enter any tracked production artifact. Unit tests remain the negative controls.
report_tracked_matches \
  "tracked production artifacts contain deployment rollback primitives" \
  'docker[[:space:]]+service[[:space:]]+rollback|update-failure-action[=:[:space:]]+rollback|rollback[_-](config|order|parallelism|monitor|max-failure-ratio)|--rollback|/UNDO-|[Uu]ndo:[[:space:]]'
report_tracked_matches \
  "tracked production artifacts contain an executable schema downgrade command" \
  'alembic[[:space:]]+downgrade|goose[[:space:]]+down|migrate[[:space:]]+down'

# Known backward authority controls are deleted, not merely left false.
report_tracked_matches \
  "a removed gateway authority switch was reintroduced" \
  'GatewayDirectPushDispatchOptions|GatewayDirectDispatch|MatchingMirror|TopicFallbackWhenEmpty|Notifications[:_]+NewRequestFanout[:_]+Enabled'

python3 - <<'PY' || failed=1
import json
from pathlib import Path

production = json.loads(Path("src/JeebGateway/appsettings.Production.json").read_text())
flags = production["FeatureFlags"]
expected = {
    "FeatureFlags.UseUpstream.Delivery": flags["UseUpstream"]["Delivery"] is True,
    "FeatureFlags.UseUpstream.Ratings": flags["UseUpstream"]["Ratings"] is True,
    "FeatureFlags.Heartbeat.Enabled": flags["Heartbeat"]["Enabled"] is False,
    "FeatureFlags.AdminAuditMode": flags["AdminAuditMode"] == "dual-write-local-read",
}
bad = [name for name, valid in expected.items() if not valid]
if bad:
    raise SystemExit("FAIL: production authority lock drifted: " + ", ".join(bad))

workflow = Path(".github/workflows/deploy-to-jeeb.yml").read_text()
for forbidden in (
    "durable_requests:",
    "inputs.durable_requests",
    "useupstreamdelivery)",
    "heartbeat)",
):
    if forbidden in workflow:
        raise SystemExit(f"FAIL: production workflow exposes backward authority input: {forbidden}")

required_counts = {
    "FeatureFlags__DurableRequests__Enabled='false'": 2,
    "FeatureFlags__Heartbeat__Enabled='false'": 2,
    "FeatureFlags__UseUpstream__Delivery='true'": 2,
    "FeatureFlags__UseUpstream__Ratings='true'": 2,
}
for token, count in required_counts.items():
    actual = workflow.count(token)
    if actual != count:
        raise SystemExit(
            f"FAIL: production workflow lock {token!r} occurs {actual} times; expected {count}"
        )

staging = Path(".github/workflows/jeeb-staging-deploy.yml").read_text()
if "add_env FeatureFlags__DurableRequests__Enabled true" not in staging:
    raise SystemExit("FAIL: staging DurableRequests forward authority lock drifted")

program = Path("src/JeebGateway/Program.cs").read_text()
for guard in (
    "AdminAuditMode cannot move behind the settled production rung",
    "Production authority is settled: FeatureFlags:UseUpstream:Delivery",
):
    if guard not in program:
        raise SystemExit(f"FAIL: production startup guard missing: {guard}")

print("Production and staging authority locks are exact.")
PY

if [[ "$failed" -ne 0 ]]; then
  echo "Forward-only authority audit FAILED"
  exit 1
fi

echo "Forward-only authority audit PASSED"
