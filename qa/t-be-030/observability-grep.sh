#!/usr/bin/env bash
# =====================================================================
# T-BE-030 / JEB-66 — AC4 observability grep
#
# Asserts the cancel.policy_applied structured log line is emitted on
# every cancellation path:
#   * cancel_free        — client cancel below the soft limit
#   * cancel_with_fee    — client cancel at/above the soft limit
#   * rate_limited       — client cancel beyond the hard limit (no cancel)
#   * cancel_strike      — jeeber cancel, strike threshold not yet tripped
#   * cancel_strike_suspended — jeeber cancel, threshold tripped, role suspended
#
# Usage:
#   ./qa/t-be-030/observability-grep.sh <path-to-test-log>
#
# Example (CI):
#   dotnet test tests/JeebGateway.IntegrationTests \
#       --logger "console;verbosity=normal" 2>&1 | tee /tmp/jeb-test.log
#   ./qa/t-be-030/observability-grep.sh /tmp/jeb-test.log
# =====================================================================
set -euo pipefail

log="${1:?usage: $0 <path-to-test-log>}"

required=(
  "cancel.policy_applied .* role=client .* action=cancel_free"
  "cancel.policy_applied .* role=client .* action=cancel_with_fee"
  "cancel.policy_applied .* role=client .* action=rate_limited"
  "cancel.policy_applied .* role=jeeber .* action=cancel_strike"
  "cancel.policy_applied .* role=jeeber .* action=cancel_strike_suspended"
)

missing=0
for pat in "${required[@]}"; do
  if ! grep -E "$pat" "$log" > /dev/null; then
    echo "MISSING: $pat" >&2
    missing=1
  else
    echo "FOUND:   $pat"
  fi
done

if [[ $missing -ne 0 ]]; then
  echo "AC4 FAILED — cancel.policy_applied is not emitting every required action variant." >&2
  exit 1
fi

echo "AC4 PASSED — cancel.policy_applied covers every policy outcome."
