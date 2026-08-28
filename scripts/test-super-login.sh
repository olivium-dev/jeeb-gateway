#!/usr/bin/env bash
set -euo pipefail
umask 077

# Redacted staging smoke for the full Dev Tool and Super Login Plus contract.
# It never prints or leaves behind response bodies, JWTs, refresh tokens, or passcodes.
# Optional inputs:
#   $1: gateway origin (default: staging public origin)
#   SUPER_LOGIN_PASSCODE: optional basic user-id-login passcode. When omitted,
#     the first configured demo-roster row is used when one is available.

test_root=''
passcode_file=''
cleanup() {
  local status=$?
  [ -z "$passcode_file" ] || rm -f -- "$passcode_file"
  [ -z "$test_root" ] || rm -rf -- "$test_root"
  return "$status"
}
trap cleanup EXIT

# SUPER_LOGIN_PASSCODE may arrive exported by the workflow. Persist it with
# mode 0600 using shell builtins, then remove it from the environment before
# even the first utility process can inherit it.
if [ "${SUPER_LOGIN_PASSCODE+x}" = x ]; then
  passcode_directory=${TMPDIR:-/tmp}
  for passcode_attempt in 1 2 3 4 5 6 7 8; do
    passcode_file="${passcode_directory%/}/jeeb-super-login-passcode.$$.$RANDOM.$passcode_attempt"
    set -o noclobber
    if : > "$passcode_file" 2>/dev/null; then
      printf '%s' "$SUPER_LOGIN_PASSCODE" >| "$passcode_file"
      break
    fi
    set +o noclobber
    passcode_file=''
  done
  set +o noclobber
  [ -n "$passcode_file" ] || {
    unset SUPER_LOGIN_PASSCODE
    echo 'Unable to allocate the private passcode file' >&2
    exit 1
  }
  unset SUPER_LOGIN_PASSCODE
fi

readonly GATEWAY_ORIGIN=${1:-https://app.jeeb.fds-1.com}

for command in curl jq python3; do
  command -v "$command" >/dev/null || {
    echo "Missing required command: $command" >&2
    exit 1
  }
done

test_root=$(mktemp -d)
chmod 700 "$test_root"
full_roster="$test_root/full-roster.json"
demo_roster="$test_root/demo-roster.json"
dev_users="$test_root/dev-users.json"
seed_body="$test_root/seed-body.json"
seed_response="$test_root/seed-response.json"
seeded_users="$test_root/seeded-users.json"
seeded_user="$test_root/seeded-user.json"
request_body="$test_root/request.json"
response_body="$test_root/response.json"
access_token_file="$test_root/access-token"
refresh_body="$test_root/refresh.json"
refresh_response="$test_root/refresh-response.json"
refresh_token_file="$test_root/refresh-token"
rotated_refresh_token_file="$test_root/rotated-refresh-token"
revoke_body="$test_root/revoke.json"
swagger_body="$test_root/swagger.json"
basic_login_body="$test_root/basic-login.json"
basic_login_response="$test_root/basic-login-response.json"
basic_identity="$test_root/basic-identity.json"
basic_access_token_file="$test_root/basic-access-token"
auth_config="$test_root/curl-auth.config"
[ -z "$passcode_file" ] || chmod 600 "$passcode_file"

request() {
  local destination=$1
  shift
  curl --silent --show-error --connect-timeout 10 --max-time 30 \
    --output "$destination" --write-out '%{http_code}' "$@"
}

expect_status() {
  local expected=$1 description=$2 destination=$3
  shift 3
  local status
  status=$(request "$destination" "$@")
  if [ "$status" != "$expected" ]; then
    echo "FAIL: ${description} expected HTTP ${expected}, received ${status}" >&2
    exit 1
  fi
}

validate_gateway_token() {
  local expected_user_id=$1 required_role=${2:-} token_file=$3
  python3 - "$expected_user_id" "$required_role" "$token_file" <<'PY'
import base64
import json
import sys
import time

expected_user_id = sys.argv[1]
required_role = sys.argv[2]
with open(sys.argv[3], encoding="utf-8") as stream:
    token = stream.read().strip()
parts = token.split(".")
if len(parts) != 3:
    raise SystemExit("JWT is not a compact three-part token")
payload = parts[1] + "=" * (-len(parts[1]) % 4)
claims = json.loads(base64.urlsafe_b64decode(payload.encode()))
audience = claims.get("aud", [])
if isinstance(audience, str):
    audience = [audience]
roles = claims.get("roles", [])
if isinstance(roles, str):
    roles = [roles]
now = int(time.time())
if claims.get("iss") != "jeeb-gateway":
    raise SystemExit("JWT issuer is not jeeb-gateway")
if "jeeb-clients" not in audience:
    raise SystemExit("JWT audience is not jeeb-clients")
if claims.get("sub") != expected_user_id:
    raise SystemExit("JWT subject does not match the selected user")
if required_role and required_role not in roles:
    raise SystemExit("JWT is missing the required role")
remaining = int(claims.get("exp", 0)) - now
if not 12 * 60 <= remaining <= 16 * 60:
    raise SystemExit("JWT lifetime is outside the staging 15-minute contract")
PY
}

expect_status 200 'full Super Login roster' "$full_roster" \
  "$GATEWAY_ORIGIN/api/User/super-login/users"
jq -e '
  (.users | type == "array" and length > 0)
  and ([.. | objects | has("passcode")] | any | not)
' "$full_roster" >/dev/null || {
  echo 'FAIL: full Super Login roster is empty, malformed, or leaks a passcode field' >&2
  exit 1
}

expect_status 200 'configured demo roster' "$demo_roster" \
  "$GATEWAY_ORIGIN/api/User/demo-users"
jq -e '.users | type == "array"' "$demo_roster" >/dev/null || {
  echo 'FAIL: configured demo roster is malformed' >&2
  exit 1
}

expect_status 200 'Dev Tool user directory' "$dev_users" \
  "$GATEWAY_ORIGIN/dev/data/users"
jq -e '.users | type == "array"' "$dev_users" >/dev/null || {
  echo 'FAIL: Dev Tool user directory is malformed' >&2
  exit 1
}

run_tag="devtool-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}"
seed_phone="+1555${GITHUB_RUN_ID:-0}${GITHUB_RUN_ATTEMPT:-0}"
jq -n --arg run_id "$run_tag" --arg phone "$seed_phone" '{
  role:"admin",
  phone:$phone,
  displayName:("Dev Tool smoke " + $run_id),
  runId:$run_id,
  tags:["staging-devtool-smoke"]
}' > "$seed_body"
chmod 600 "$seed_body"
expect_status 200 'Dev Tool user seed' "$seed_response" \
  --request POST --header 'Content-Type: application/json' \
  --data-binary "@$seed_body" "$GATEWAY_ORIGIN/dev/seed/user"
user_id=$(jq -er '.userId | select(type == "string" and length > 0)' \
  "$seed_response")

expect_status 200 'seeded Dev Tool user directory lookup' "$seeded_users" \
  "$GATEWAY_ORIGIN/dev/data/users?runId=$run_tag"
jq -e --arg user_id "$user_id" \
  'any(.users[]?; .userId == $user_id)' "$seeded_users" >/dev/null || {
  echo 'FAIL: the seeded user is absent from the filtered Dev Tool directory' >&2
  exit 1
}
expect_status 200 'seeded Dev Tool single-user lookup' "$seeded_user" \
  "$GATEWAY_ORIGIN/dev/data/user/$user_id"
jq -e --arg user_id "$user_id" '.userId == $user_id' "$seeded_user" >/dev/null || {
  echo 'FAIL: the single-user Dev Tool lookup returned a different identity' >&2
  exit 1
}
expect_status 200 'full roster after Dev Tool seed' "$full_roster" \
  "$GATEWAY_ORIGIN/api/User/super-login/users"
jq -e --arg user_id "$user_id" \
  'any(.users[]?; .userId == $user_id)' "$full_roster" >/dev/null || {
  echo 'FAIL: the seeded user is absent from the full Super Login roster' >&2
  exit 1
}

jq -n --arg user_id "$user_id" \
  '{userId:$user_id,roles:["admin"]}' > "$request_body"
chmod 600 "$request_body"
expect_status 200 'credential-less Super Login Plus mint' "$response_body" \
  --request POST --header 'Content-Type: application/json' \
  --data-binary "@$request_body" "$GATEWAY_ORIGIN/auth/tokens"
jq -er '.accessToken | select(type == "string" and length > 0)' \
  "$response_body" > "$access_token_file"
jq -er '.refreshToken | select(type == "string" and length > 0)' \
  "$response_body" > "$refresh_token_file"
validate_gateway_token "$user_id" admin "$access_token_file"

printf 'header = "Authorization: Bearer %s"\n' "$(<"$access_token_file")" > "$auth_config"
chmod 600 "$auth_config"
expect_status 404 'anonymous Swagger document concealment' "$swagger_body" \
  "$GATEWAY_ORIGIN/swagger/v1/swagger.json"
expect_status 200 'admin-gated Swagger document' "$swagger_body" \
  --config "$auth_config" "$GATEWAY_ORIGIN/swagger/v1/swagger.json"
jq -e '
  .paths["/auth/tokens"].post
  and .paths["/dev/seed/user"].post
  and .paths["/dev/data/users"].get
  and .paths["/api/User/user-id-login"].post
' "$swagger_body" >/dev/null || {
  echo 'FAIL: Swagger omits one or more Dev Tool or Super Login routes' >&2
  exit 1
}

jq -n --rawfile refresh_token "$refresh_token_file" \
  '{refreshToken:($refresh_token | rtrimstr("\n"))}' > "$refresh_body"
chmod 600 "$refresh_body"
expect_status 200 'refresh rotation' "$refresh_response" \
  --request POST --header 'Content-Type: application/json' \
  --data-binary "@$refresh_body" "$GATEWAY_ORIGIN/auth/tokens/refresh"
jq -er '.refreshToken | select(type == "string" and length > 0)' \
  "$refresh_response" > "$rotated_refresh_token_file"
if cmp -s "$rotated_refresh_token_file" "$refresh_token_file"; then
  echo 'FAIL: refresh token did not rotate' >&2
  exit 1
fi
jq -n --rawfile refresh_token "$rotated_refresh_token_file" \
  '{refreshToken:($refresh_token | rtrimstr("\n"))}' > "$revoke_body"
chmod 600 "$revoke_body"
expect_status 204 'current refresh-token revocation' "$response_body" \
  --request POST --header 'Content-Type: application/json' \
  --data-binary "@$revoke_body" "$GATEWAY_ORIGIN/auth/tokens/revoke"

if [ -s "$passcode_file" ]; then
  jq -n --arg user_id "$user_id" --rawfile passcode "$passcode_file" \
    '{userId:$user_id,passcode:$passcode}' > "$basic_identity"
elif jq -e '
    first(.users[]? | select(
      (.userId | type == "string" and length > 0)
      and (.passcode | type == "string" and length > 0)
    ) | {userId,passcode}) // empty
  ' "$demo_roster" > "$basic_identity"; then
  :
else
  : > "$basic_identity"
fi

if jq -e '
  (.userId | type == "string" and length > 0)
  and (.passcode | type == "string" and length > 0)
' "$basic_identity" >/dev/null 2>&1; then
  basic_user_id=$(jq -er '.userId' "$basic_identity")
  jq '{userId,superAdminPassCode:.passcode}' \
    "$basic_identity" > "$basic_login_body"
  chmod 600 "$basic_login_body"
  expect_status 200 'basic user-id-login' "$basic_login_response" \
    --request POST --header 'Content-Type: application/json' \
    --data-binary "@$basic_login_body" "$GATEWAY_ORIGIN/api/User/user-id-login"
  jq -er '.authToken | select(type == "string" and length > 0)' \
    "$basic_login_response" > "$basic_access_token_file"
  validate_gateway_token "$basic_user_id" '' "$basic_access_token_file"
  echo 'PASS: basic user-id-login returned a gateway-audience session.'
else
  echo 'INFO: basic user-id-login skipped because no configured demo passcode was available.'
fi

echo 'PASS: full staging Dev Tool, Super Login Plus, token lifecycle, and Swagger contracts are exact.'
