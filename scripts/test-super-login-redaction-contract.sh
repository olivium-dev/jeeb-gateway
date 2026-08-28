#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/test-super-login.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir "$fake_bin"
canary="$test_root/first-child"

cat > "$fake_bin/mktemp" <<'MKTEMP'
#!/usr/bin/env bash
set -euo pipefail
if [ "${SUPER_LOGIN_PASSCODE+x}" = x ]; then
  printf '%s\n' leaked > "$PASSCODE_CANARY"
else
  printf '%s\n' redacted > "$PASSCODE_CANARY"
fi
exit 73
MKTEMP
chmod +x "$fake_bin/mktemp"

set +e
SUPER_LOGIN_PASSCODE='process-environment-canary' PASSCODE_CANARY="$canary" \
  PATH="$fake_bin:$PATH" bash "$subject" https://example.invalid \
  >/dev/null 2>&1
status=$?
set -e
[ "$status" -ne 0 ]
[ "$(<"$canary")" = redacted ] || {
  echo 'SUPER_LOGIN_PASSCODE reached the first child process' >&2
  exit 1
}
if rg -n -- '--arg[[:space:]]+(passcode|access_token|refresh_token)' "$subject" >/dev/null; then
  echo 'Super Login smoke puts a credential in child argv' >&2
  exit 1
fi

echo 'Super Login smoke redaction contract: PASS (passcode absent from first child and credential argv)'
