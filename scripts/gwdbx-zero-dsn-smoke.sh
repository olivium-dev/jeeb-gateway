#!/usr/bin/env bash
# gwdbx W5-14 harness: prod-env cold boot with ZERO Postgres DSNs must serve the A9 final 17-name
# roster on /health/ready. Full contract + usage: docs/runbooks/gwdbx-deletion-ledger.md §7.

# Usage: gwdbx-zero-dsn-smoke.sh <publish-dir> [--control]. The --control leg boots WITH dummy DSNs
# and must see the 3 DB-era checks (anti-vacuity). Exit: 0 pass · 1 criterion unmet · 2 harness broken.
set -uo pipefail

PUBLISH_DIR="${1:?usage: gwdbx-zero-dsn-smoke.sh <publish-dir> [--control]}"
MODE="${2:-zero-dsn}"
PORT="${SMOKE_PORT:-18099}"
REDIS_CS="${SMOKE_REDIS_CS:-localhost:6379}"
ROSTER_FILE="$(dirname "$0")/gwdbx-final-health-roster.txt"
BOOT_WAIT_SECONDS="${SMOKE_BOOT_WAIT:-60}"
LOG="$(mktemp -t gwdbx-zero-dsn-smoke.XXXXXX)"

[ -f "$PUBLISH_DIR/JeebGateway.dll" ] || { echo "HARNESS-BROKEN: no JeebGateway.dll in $PUBLISH_DIR" >&2; exit 2; }
[ -f "$ROSTER_FILE" ] || { echo "HARNESS-BROKEN: missing $ROSTER_FILE" >&2; exit 2; }

# Env recipe = the END-STATE gateway's legitimate needs (Redis stays per R4; keys are smoke-only values).
# The ONLY difference between the two modes is the presence of the two Postgres DSNs.
ENV_COMMON=(
  "ASPNETCORE_ENVIRONMENT=Production"
  "ASPNETCORE_URLS=http://127.0.0.1:${PORT}"
  "Redis__ConnectionString=${REDIS_CS}"
  "GatewayRateLimit__RedisConnectionString=${REDIS_CS}"
  "FeatureFlags__DurableRequests__Enabled=true"
  "Jwt__SigningKey=zero-dsn-smoke-signing-key-not-for-prod-aaaaaaaaaaaaaaaaaaaaaaaa"
  "UmJwt__SigningKey=zero-dsn-smoke-um-signing-key-not-for-prod-aaaaaaaaaaaaaaaaaaaa"
  "Security__PhoneHash__Pepper=zero-dsn-smoke-pepper"
  "SuperLogin__OpenMode=true"
  "DemoUsers__Enabled=true"
  "Features__DevEndpoints__Enabled=false"
)
ENV_DSNS=(
  "GatewayPostgres__ConnectionString=Host=127.0.0.1;Port=5432;Database=jeeb;Username=jeeb;Password=jeeb"
  "WalletPostgres__ConnectionString=Host=127.0.0.1;Port=5432;Database=jeeb-wallet;Username=jeeb;Password=jeeb"
)

if [ "$MODE" = "--control" ]; then
  ENV_ALL=("${ENV_COMMON[@]}" "${ENV_DSNS[@]}")
else
  ENV_ALL=("${ENV_COMMON[@]}")
fi

cd "$PUBLISH_DIR"
env -i HOME="$HOME" PATH="$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 "${ENV_ALL[@]}" \
  dotnet JeebGateway.dll >"$LOG" 2>&1 &
GW_PID=$!
trap 'kill "$GW_PID" 2>/dev/null; wait "$GW_PID" 2>/dev/null' EXIT

READY_JSON=""
for _ in $(seq 1 "$BOOT_WAIT_SECONDS"); do
  if ! kill -0 "$GW_PID" 2>/dev/null; then break; fi
  READY_JSON="$(curl -sf -m 15 "http://127.0.0.1:${PORT}/health/ready" 2>/dev/null)" \
    || READY_JSON="$(curl -s -m 15 "http://127.0.0.1:${PORT}/health/ready" 2>/dev/null)"
  [ -n "$READY_JSON" ] && break
  sleep 1
done

if [ -z "$READY_JSON" ]; then
  echo "BOOT-FAILED (${MODE}): the process never served /health/ready. Last 40 log lines:"
  tail -40 "$LOG"
  if [ "$MODE" = "--control" ]; then
    echo "RESULT: HARNESS-BROKEN — the with-DSN control boot must succeed for zero-DSN results to mean anything."
    exit 2
  fi
  # Today's expected signature: StoreDurabilityGuard FAIL-CLOSED naming in-memory criticals.
  if grep -q "FAIL-CLOSED" "$LOG"; then
    echo "RESULT: CRITERION UNMET (expected until W5-11) — fail-closed guard aborted the zero-DSN boot."
  else
    echo "RESULT: CRITERION UNMET — zero-DSN boot died WITHOUT the fail-closed signature; inspect the log."
  fi
  exit 1
fi

NAMES="$(printf '%s' "$READY_JSON" | python3 -c '
import json,sys
print("\n".join(sorted(c["name"] for c in json.load(sys.stdin)["checks"])))')" \
  || { echo "HARNESS-BROKEN: /health/ready responded but the roster did not parse: $READY_JSON" >&2; exit 2; }
COUNT="$(printf '%s\n' "$NAMES" | sed '/^$/d' | wc -l | tr -d ' ')"
echo "roster (${COUNT}):"; printf '%s\n' "$NAMES"

if [ "$MODE" = "--control" ]; then
  for must in gateway-postgres wallet-postgres store-durability; do
    printf '%s\n' "$NAMES" | grep -qx "$must" \
      || { echo "RESULT: HARNESS-BROKEN — control roster is missing '$must' (probe cannot see DB checks)."; exit 2; }
  done
  echo "RESULT: CONTROL PASS — boot recipe works and the probe sees the DB checks (count=${COUNT})."
  exit 0
fi

EXPECTED="$(grep -v '^#' "$ROSTER_FILE" | sed '/^$/d' | sort)"
if [ "$NAMES" = "$EXPECTED" ]; then
  echo "RESULT: PASS — zero-DSN cold boot serves exactly the A9 final $(printf '%s\n' "$EXPECTED" | wc -l | tr -d ' ')-name roster."
  exit 0
fi
echo "RESULT: CRITERION UNMET (expected until W5-11) — roster differs from the A9 final set:"
diff <(printf '%s\n' "$EXPECTED") <(printf '%s\n' "$NAMES") | sed 's/^/  /'
exit 1
