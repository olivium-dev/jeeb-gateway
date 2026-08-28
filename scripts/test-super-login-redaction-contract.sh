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

runtime_bin="$test_root/runtime-bin"
mkdir "$runtime_bin"
access_token=$(python3 - <<'PY'
import base64
import json
import time

def encode(value):
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")

print(".".join((
    encode({"alg": "none", "typ": "JWT"}),
    encode({
        "iss": "jeeb-gateway",
        "aud": ["jeeb-clients"],
        "sub": "seeded-user",
        "roles": ["admin"],
        "exp": int(time.time()) + 900,
    }),
    "signature",
)))
PY
)
basic_access_token=$(SMOKE_SUBJECT=live-user python3 - <<'PY'
import base64
import json
import os
import time

def encode(value):
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")

print(".".join((
    encode({"alg": "none", "typ": "JWT"}),
    encode({
        "iss": "jeeb-gateway",
        "aud": ["jeeb-clients"],
        "sub": os.environ["SMOKE_SUBJECT"],
        "roles": ["client"],
        "exp": int(time.time()) + 900,
    }),
    "signature",
)))
PY
)
cat > "$runtime_bin/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
destination=
url=
has_config=false
data_source=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) destination=$2; shift 2 ;;
    --config) has_config=true; shift 2 ;;
    --data-binary) data_source=$2; shift 2 ;;
    https://*) url=$1; shift ;;
    *) shift ;;
  esac
done
[ -n "$destination" ] && [ -n "$url" ]
case "$url" in
  */api/User/super-login/users)
    printf '%s\n' '{"users":[{"userId":"live-user","name":"Live","role":"client","roles":["client"]},{"userId":"seeded-user","name":"Seeded","role":"admin","roles":["admin"]}]}' > "$destination"; status=200 ;;
  */api/User/demo-users)
    if [ "$SMOKE_DEMO_PASSCODE_AVAILABLE" = true ]; then
      printf '%s\n' '{"users":[{"userId":"stale-demo-row","name":"Demo","role":"client","passcode":"PLACEHOLDER_PASSCODE_CANARY"}]}' > "$destination"
    else
      printf '%s\n' '{"users":[]}' > "$destination"
    fi
    status=200
    ;;
  */dev/data/user/seeded-user)
    printf '%s\n' '{"userId":"seeded-user"}' > "$destination"; status=200 ;;
  */dev/data/users*)
    printf '%s\n' '{"users":[{"userId":"seeded-user"}]}' > "$destination"; status=200 ;;
  */dev/seed/user)
    printf '%s\n' '{"userId":"seeded-user"}' > "$destination"; status=200 ;;
  */auth/tokens/refresh)
    printf '{"refreshToken":"rotated-refresh"}\n' > "$destination"; status=200 ;;
  */auth/tokens/revoke)
    : > "$destination"; status=204 ;;
  */auth/tokens)
    printf '{"accessToken":"%s","refreshToken":"initial-refresh"}\n' "$SMOKE_ACCESS_TOKEN" > "$destination"; status=200 ;;
  */swagger/v1/swagger.json)
    if [ "$has_config" = true ]; then
      printf '%s\n' '{"paths":{"/auth/tokens":{"post":{}},"/dev/seed/user":{"post":{}},"/dev/data/users":{"get":{}},"/api/User/user-id-login":{"post":{}}}}' > "$destination"; status=200
    else
      : > "$destination"; status=404
    fi
    ;;
  */api/User/user-id-login)
    [ "${data_source#@}" != "$data_source" ]
    jq -e '.userId == "live-user" and .superAdminPassCode == "PLACEHOLDER_PASSCODE_CANARY"' \
      "${data_source#@}" >/dev/null
    : >> "$SMOKE_BASIC_LOGIN_CALLS"
    printf '{"authToken":"%s"}\n' "$SMOKE_BASIC_ACCESS_TOKEN" > "$destination"; status=200 ;;
  *) exit 72 ;;
esac
printf '%s' "$status"
CURL
chmod +x "$runtime_bin/curl"

runtime_log="$test_root/runtime.log"
basic_calls="$test_root/basic-calls"
SMOKE_ACCESS_TOKEN="$access_token" SMOKE_BASIC_ACCESS_TOKEN="$basic_access_token" \
  SMOKE_DEMO_PASSCODE_AVAILABLE=true SMOKE_BASIC_LOGIN_CALLS="$basic_calls" \
  PATH="$runtime_bin:$PATH" \
  bash "$subject" https://example.invalid >"$runtime_log" 2>&1
grep -Fq 'PASS: basic user-id-login returned a gateway-audience session.' "$runtime_log"
[ -e "$basic_calls" ]
grep -Fq 'PASS: required staging Dev Tool, Super Login Plus, token lifecycle, and Swagger contracts are exact; optional basic login result reported above.' "$runtime_log"
if grep -Fq 'PLACEHOLDER_PASSCODE_CANARY' "$runtime_log"; then
  echo 'Configured placeholder passcode or response body reached smoke logs' >&2
  exit 1
fi

no_passcode_log="$test_root/no-passcode.log"
rm -f -- "$basic_calls"
SMOKE_ACCESS_TOKEN="$access_token" SMOKE_BASIC_ACCESS_TOKEN="$basic_access_token" \
  SMOKE_DEMO_PASSCODE_AVAILABLE=false SMOKE_BASIC_LOGIN_CALLS="$basic_calls" \
  PATH="$runtime_bin:$PATH" \
  bash "$subject" https://example.invalid >"$no_passcode_log" 2>&1
grep -Fq 'basic user-id-login skipped because no configured demo passcode was available' "$no_passcode_log"
[ ! -e "$basic_calls" ]

echo 'Super Login smoke redaction contract: PASS (credentials redacted; shared passcode uses a live roster identity; only absent passcode skips)'
