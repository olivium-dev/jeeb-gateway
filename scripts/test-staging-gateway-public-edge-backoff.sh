#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/staging-gateway-public-edge-backoff.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir "$fake_bin"

cat > "$test_root/probe.sh" <<'PROBE'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  devtool) [ "$#" -eq 1 ] ;;
  posture|devtool-posture) [ "$#" -eq 6 ] ;;
  *) exit 64 ;;
esac
count=0
[ ! -s "$PUBLIC_EDGE_CALLS" ] || count=$(<"$PUBLIC_EDGE_CALLS")
count=$((count + 1))
printf '%s\n' "$count" > "$PUBLIC_EDGE_CALLS"
printf '%s\n' 'RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED'
[ "$count" -ge "$PUBLIC_EDGE_SUCCESS_AT" ]
PROBE
cat > "$fake_bin/sleep" <<'SLEEP'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1" >> "$PUBLIC_EDGE_SLEEPS"
SLEEP
chmod +x "$test_root/probe.sh" "$fake_bin/sleep"

export PATH="$fake_bin:$PATH"
export PUBLIC_EDGE_CALLS="$test_root/calls"
export PUBLIC_EDGE_SLEEPS="$test_root/sleeps"
export PUBLIC_EDGE_SUCCESS_AT=3
transient_log="$test_root/transient.log"
bash "$subject" "$test_root/probe.sh" >"$transient_log" 2>&1
[ "$(<"$PUBLIC_EDGE_CALLS")" -eq 3 ]
[ "$(tr '\n' ' ' < "$PUBLIC_EDGE_SLEEPS")" = '1 2 ' ]
grep -Fq 'attempt=3/8 result=passed (redacted)' "$transient_log"
if grep -Fq 'RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED' "$transient_log"; then exit 1; fi

: > "$PUBLIC_EDGE_CALLS"
: > "$PUBLIC_EDGE_SLEEPS"
export PUBLIC_EDGE_SUCCESS_AT=9
terminal_log="$test_root/terminal.log"
if bash "$subject" "$test_root/probe.sh" >"$terminal_log" 2>&1; then
  echo 'public-edge backoff accepted eight failed probes' >&2
  exit 1
fi
[ "$(<"$PUBLIC_EDGE_CALLS")" -eq 8 ]
[ "$(tr '\n' ' ' < "$PUBLIC_EDGE_SLEEPS")" = '1 2 4 8 8 8 8 ' ]
grep -Fq 'attempts=8 result=terminal-failure (redacted)' "$terminal_log"
if grep -Fq 'RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED' "$terminal_log"; then exit 1; fi

: > "$PUBLIC_EDGE_CALLS"
: > "$PUBLIC_EDGE_SLEEPS"
posture_log="$test_root/posture.log"
if bash "$subject" "$test_root/probe.sh" devtool-posture true true true true true \
    >"$posture_log" 2>&1; then
  echo 'recovery posture backoff accepted eight failed probes under an if-condition' >&2
  exit 1
fi
[ "$(<"$PUBLIC_EDGE_CALLS")" -eq 8 ]
grep -Fq 'phase=recovery-devtool-posture attempts=8 result=terminal-failure (redacted)' \
  "$posture_log"
if grep -Fq 'RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED' "$posture_log"; then exit 1; fi

echo 'staging public-edge backoff: PASS (Dev Tool recovery, terminal failure, recovery-posture failure, 1/2/4/8 cap, no bodies)'
