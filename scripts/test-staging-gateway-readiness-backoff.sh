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
[ "$count" -ge "$READINESS_SUCCESS_AT" ]
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

echo 'staging gateway readiness backoff: PASS (1/2/4/8 cap, 20 attempts, no final sleep)'
