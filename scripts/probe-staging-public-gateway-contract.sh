#!/usr/bin/env bash
set -euo pipefail

readonly APP_ORIGIN=https://app.jeeb.fds-1.com
readonly CMS_ORIGIN=https://cms.jeeb.fds-1.com
readonly CSRF_TYPE=https://jeeb.dev/errors/csrf_rejected
readonly ORIGIN_TYPE=https://jeeb.dev/errors/origin_rejected
readonly INVALID_MINT_CREDENTIAL=invalid-staging-mint-probe-credential

request() {
  local response_file=$1
  shift

  curl --silent --show-error --connect-timeout 10 --max-time 20 \
    --output "$response_file" --write-out '%{http_code}' "$@"
}

expect_status() {
  local expected=$1 description=$2
  shift 2
  local response_file status

  response_file=$(mktemp)
  status=$(request "$response_file" "$@")
  if [ "$status" != "$expected" ]; then
    echo "${description}: expected HTTP ${expected}, received ${status}" >&2
    rm -f -- "$response_file"
    return 1
  fi
  rm -f -- "$response_file"
}

probe_origin() {
  local host=$1 path=$2 supplied_scheme=$3 expected_type=$4
  local response_file status

  response_file=$(mktemp)
  status=$(request "$response_file" --request POST \
    --header "Origin: ${supplied_scheme}://${host}" \
    --header 'Sec-Fetch-Site: same-origin' \
    "https://${host}${path}")
  if [ "$status" != 403 ]; then
    echo "${host}${path}: expected HTTP 403, received ${status}" >&2
    rm -f -- "$response_file"
    return 1
  fi
  if ! jq -e --arg expected "$expected_type" '.type == $expected' \
    "$response_file" >/dev/null; then
    rm -f -- "$response_file"
    return 1
  fi
  rm -f -- "$response_file"
}

probe_origin app.jeeb.fds-1.com /admin/v1/auth/refresh https "$CSRF_TYPE"
probe_origin app.jeeb.fds-1.com /admin/v1/auth/refresh http "$ORIGIN_TYPE"
probe_origin "${CMS_ORIGIN#https://}" /gateway/admin/v1/auth/refresh https "$CSRF_TYPE"
probe_origin "${CMS_ORIGIN#https://}" /gateway/admin/v1/auth/refresh http "$ORIGIN_TYPE"

for retired_path in /api/User/demo-users /api/User/super-login/users; do
  expect_status 404 "Public debug login surface ${retired_path}" \
    "${APP_ORIGIN}${retired_path}"
done

expect_status 400 'OTP request validation contract' \
  --header 'Content-Type: application/json' --data '{}' \
  "${APP_ORIGIN}/v1/auth/otp/request"

# These probes intentionally carry no user identity and never handle the real mint
# credential. The fixed invalid canary is public test data, not a secret.
expect_status 401 'Token mint without a privileged credential' \
  --header 'Content-Type: application/json' --data '{}' \
  "${APP_ORIGIN}/auth/tokens"
expect_status 403 'Token mint with an invalid privileged credential' \
  --header 'Content-Type: application/json' \
  --header "X-Service-Auth-Key: ${INVALID_MINT_CREDENTIAL}" --data '{}' \
  "${APP_ORIGIN}/auth/tokens"

echo 'Staging public origin, CSRF, retired-demo, OTP, and token-mint contracts are exact.'
