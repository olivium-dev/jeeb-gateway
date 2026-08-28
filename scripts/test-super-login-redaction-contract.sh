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
runtime_state="$test_root/runtime-state"
mkdir "$runtime_bin" "$runtime_state"
readonly live_user_id='11111111-1111-4111-8111-111111111111'
readonly seeded_user_id='22222222-2222-4222-8222-222222222222'

make_token() {
  local subject=$1 role=$2
  SMOKE_SUBJECT="$subject" SMOKE_ROLE="$role" python3 - <<'PY'
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
        "roles": [os.environ["SMOKE_ROLE"]],
        "exp": int(time.time()) + 900,
    }),
    "signature",
)))
PY
}

seed_access_token=$(make_token "$seeded_user_id" admin)
seed_rotated_access_token=$(make_token "$seeded_user_id" admin)
basic_access_token=$(make_token "$live_user_id" client)
basic_rotated_access_token=$(make_token "$live_user_id" client)

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
    if [ -e "$SMOKE_STATE_DIR/seeded" ]; then
      printf '%s\n' "{\"users\":[{\"userId\":\"$SMOKE_LIVE_USER_ID\",\"name\":\"Live\",\"role\":\"client\",\"roles\":[\"client\"]},{\"userId\":\"$SMOKE_SEEDED_USER_ID\",\"name\":\"Seeded\",\"role\":\"admin\",\"roles\":[\"admin\"]}]}" > "$destination"
    else
      printf '%s\n' "{\"users\":[{\"userId\":\"$SMOKE_LIVE_USER_ID\",\"name\":\"Live\",\"role\":\"client\",\"roles\":[\"client\"]}]}" > "$destination"
    fi
    status=200
    ;;
  */api/User/demo-users)
    printf '%s\n' '{"users":[]}' > "$destination"; status=200 ;;
  */dev/data/user/*)
    [ "${url##*/}" = "$SMOKE_SEEDED_USER_ID" ]
    printf '%s\n' "{\"userId\":\"$SMOKE_SEEDED_USER_ID\"}" > "$destination"; status=200 ;;
  */dev/data/users*)
    printf '%s\n' "{\"users\":[{\"userId\":\"$SMOKE_SEEDED_USER_ID\"}]}" > "$destination"; status=200 ;;
  */dev/seed/user)
    : > "$SMOKE_STATE_DIR/seeded"
    printf '%s\n' "{\"userId\":\"$SMOKE_SEEDED_USER_ID\"}" > "$destination"; status=200 ;;
  */auth/tokens/refresh)
    [ "${data_source#@}" != "$data_source" ]
    refresh_token=$(jq -er '.refreshToken' "${data_source#@}")
    case "$refresh_token" in
      MINT_INITIAL_REFRESH_CANARY)
        printf '{"accessToken":"%s","refreshToken":"MINT_ROTATED_REFRESH_CANARY"}\n' \
          "$SMOKE_SEED_ROTATED_ACCESS_TOKEN" > "$destination"
        ;;
      BASIC_INITIAL_REFRESH_CANARY)
        : > "$SMOKE_STATE_DIR/basic-refresh"
        printf '{"accessToken":"%s","refreshToken":"BASIC_ROTATED_REFRESH_CANARY"}\n' \
          "$SMOKE_BASIC_ROTATED_ACCESS_TOKEN" > "$destination"
        ;;
      *) exit 71 ;;
    esac
    status=200
    ;;
  */auth/tokens/revoke)
    [ "${data_source#@}" != "$data_source" ]
    refresh_token=$(jq -er '.refreshToken' "${data_source#@}")
    case "$refresh_token" in
      MINT_ROTATED_REFRESH_CANARY) : > "$SMOKE_STATE_DIR/mint-revoke" ;;
      BASIC_ROTATED_REFRESH_CANARY) : > "$SMOKE_STATE_DIR/basic-revoke" ;;
      *) exit 70 ;;
    esac
    : > "$destination"; status=204
    ;;
  */auth/tokens)
    printf '{"accessToken":"%s","refreshToken":"MINT_INITIAL_REFRESH_CANARY"}\n' \
      "$SMOKE_SEED_ACCESS_TOKEN" > "$destination"; status=200 ;;
  */swagger/v1/swagger.json)
    if [ "$has_config" = true ]; then
      printf '%s\n' '{"paths":{"/auth/tokens":{"post":{}},"/dev/seed/user":{"post":{}},"/dev/data/users":{"get":{}},"/api/User/user-id-login":{"post":{}}}}' > "$destination"; status=200
    else
      : > "$destination"; status=404
    fi
    ;;
  */api/User/user-id-login)
    [ "${data_source#@}" != "$data_source" ]
    jq -e --arg user_id "$SMOKE_LIVE_USER_ID" '
      .userId == $user_id
      and (keys == ["userId"])
      and (has("superAdminPassCode") | not)
    ' "${data_source#@}" >/dev/null
    : > "$SMOKE_STATE_DIR/basic-login"
    printf '{"userId":"%s","authToken":"%s","refreshToken":"BASIC_INITIAL_REFRESH_CANARY"}\n' \
      "$SMOKE_LIVE_USER_ID" "$SMOKE_BASIC_ACCESS_TOKEN" > "$destination"; status=200 ;;
  *) exit 72 ;;
esac
printf '%s' "$status"
CURL
chmod +x "$runtime_bin/curl"

runtime_log="$test_root/runtime.log"
SMOKE_LIVE_USER_ID="$live_user_id" \
  SMOKE_SEEDED_USER_ID="$seeded_user_id" \
  SMOKE_SEED_ACCESS_TOKEN="$seed_access_token" \
  SMOKE_SEED_ROTATED_ACCESS_TOKEN="$seed_rotated_access_token" \
  SMOKE_BASIC_ACCESS_TOKEN="$basic_access_token" \
  SMOKE_BASIC_ROTATED_ACCESS_TOKEN="$basic_rotated_access_token" \
  SMOKE_STATE_DIR="$runtime_state" \
  PATH="$runtime_bin:$PATH" \
  bash "$subject" https://example.invalid >"$runtime_log" 2>&1

grep -Fq 'PASS: basic user-id-login returned a gateway-audience session and its exact refresh token rotated and revoked.' "$runtime_log"
grep -Fq 'PASS: required staging Dev Tool, Super Login Plus, both token lifecycles, and Swagger contracts are exact.' "$runtime_log"
[ -e "$runtime_state/basic-login" ]
[ -e "$runtime_state/basic-refresh" ]
[ -e "$runtime_state/basic-revoke" ]
[ -e "$runtime_state/mint-revoke" ]
if grep -Eq 'MINT_(INITIAL|ROTATED)_REFRESH_CANARY|BASIC_(INITIAL|ROTATED)_REFRESH_CANARY|process-environment-canary' "$runtime_log"; then
  echo 'A refresh token or passcode canary reached smoke logs' >&2
  exit 1
fi

echo 'Super Login smoke redaction contract: PASS (OpenMode omits passcode; pre-existing canonical identity login and exact refresh rotation/revocation verified; credentials redacted)'
