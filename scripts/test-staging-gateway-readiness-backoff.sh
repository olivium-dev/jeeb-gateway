#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/staging-gateway-readiness-backoff.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir "$fake_bin"

cat > "$fake_bin/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
count=0
[ ! -s "$READINESS_CALLS" ] || count=$(<"$READINESS_CALLS")
count=$((count + 1))
printf '%s\n' "$count" > "$READINESS_CALLS"
[ "$count" -ge "$READINESS_SUCCESS_AT" ] || exit 22
if [[ "${@: -1}" = */tiers ]]; then
  [ "${TIERS_HTTP_SUCCESS:-true}" = true ] || exit 22
  printf '%s' "${TIERS_BODY:-}"
else
  printf '%s' "${READINESS_BODY:-}"
fi
CURL
cat > "$fake_bin/sleep" <<'SLEEP'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1" >> "$READINESS_SLEEPS"
SLEEP
chmod +x "$fake_bin/curl" "$fake_bin/sleep"

export PATH="$fake_bin:$PATH"
export READINESS_CALLS="$test_root/calls"
export READINESS_SLEEPS="$test_root/sleeps"
export READINESS_SUCCESS_AT=5
bash "$subject" 10000 /health/ready
[ "$(<"$READINESS_CALLS")" -eq 5 ]
[ "$(tr '\n' ' ' < "$READINESS_SLEEPS")" = '1 2 4 8 ' ]

: > "$READINESS_CALLS"
: > "$READINESS_SLEEPS"
export READINESS_SUCCESS_AT=21
if bash "$subject" 10000 /health/ready; then
  echo 'readiness backoff accepted 20 failed probes' >&2
  exit 1
fi
[ "$(<"$READINESS_CALLS")" -eq 20 ]
[ "$(wc -l < "$READINESS_SLEEPS" | tr -d ' ')" -eq 19 ]
[ "$(tail -n 1 "$READINESS_SLEEPS")" -eq 8 ]
[ "$(grep -c '^8$' "$READINESS_SLEEPS")" -eq 16 ]

export READINESS_SUCCESS_AT=1
export READINESS_BODY='{"status":"Degraded","checks":[{"name":"credential-delivery-service-token","status":"Healthy"},{"name":"delivery-service","status":"Healthy"},{"name":"chat-upstream-readiness","status":"Degraded"}]}'
export TIERS_BODY='{"items":[]}'
: > "$READINESS_CALLS"
: > "$READINESS_SLEEPS"
bash "$subject" 10000 /health/ready catalog
[ "$(<"$READINESS_CALLS")" -eq 2 ]
[ ! -s "$READINESS_SLEEPS" ]
: > "$READINESS_CALLS"
bash "$subject" 10000 /health/ready credential
[ "$(<"$READINESS_CALLS")" -eq 1 ]

for scenario in degraded missing duplicate malformed owner_unhealthy tiers_http tiers_shape; do
  (
    case "$scenario" in
      degraded) READINESS_BODY='{"checks":[{"name":"credential-delivery-service-token","status":"Degraded"},{"name":"delivery-service","status":"Healthy"}]}' ;;
      missing) READINESS_BODY='{"checks":[{"name":"delivery-service","status":"Healthy"}]}' ;;
      duplicate) READINESS_BODY='{"checks":[{"name":"credential-delivery-service-token","status":"Healthy"},{"name":"credential-delivery-service-token","status":"Healthy"},{"name":"delivery-service","status":"Healthy"}]}' ;;
      malformed) READINESS_BODY='not-json' ;;
      owner_unhealthy) READINESS_BODY='{"checks":[{"name":"credential-delivery-service-token","status":"Healthy"},{"name":"delivery-service","status":"Unhealthy"}]}' ;;
      tiers_http) export TIERS_HTTP_SUCCESS=false ;;
      tiers_shape) TIERS_BODY='{"status":"Healthy"}' ;;
    esac
    : > "$READINESS_CALLS"
    : > "$READINESS_SLEEPS"
    if bash "$subject" 10000 /health/ready catalog > /dev/null 2>&1; then
      echo "readiness accepted $scenario" >&2
      exit 1
    fi
    [ "$(wc -l < "$READINESS_SLEEPS" | tr -d ' ')" -eq 19 ]
  )
done

echo 'staging gateway readiness backoff: PASS (bounded retries, delivery rows, authenticated catalog, optional degraded services)'
