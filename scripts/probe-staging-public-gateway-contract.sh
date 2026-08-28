#!/usr/bin/env bash
set -euo pipefail

readonly APP_ORIGIN=https://app.jeeb.fds-1.com
readonly CMS_ORIGIN=https://cms.jeeb.fds-1.com
readonly CSRF_TYPE=https://jeeb.dev/errors/csrf_rejected
readonly ORIGIN_TYPE=https://jeeb.dev/errors/origin_rejected
readonly PROBE_MODE=${1:-invariant}

case "$PROBE_MODE" in
  invariant)
    expected_open_mode=''
    expected_demo_users=''
    expected_dev_endpoints=''
    expected_swagger=''
    expected_token_mint=''
    ;;
  devtool)
    expected_open_mode=true
    expected_demo_users=true
    expected_dev_endpoints=true
    expected_swagger=true
    expected_token_mint=true
    ;;
  posture)
    expected_open_mode=${2:-}
    expected_demo_users=${3:-}
    expected_dev_endpoints=${4:-}
    expected_swagger=${5:-}
    expected_token_mint=${6:-}
    for expected_boolean in "$expected_open_mode" "$expected_demo_users" \
      "$expected_dev_endpoints" "$expected_swagger" "$expected_token_mint"; do
      case "$expected_boolean" in true|false) ;; *)
        echo 'Posture expectations must be exact true/false values' >&2
        exit 64
        ;;
      esac
    done
    ;;
  *)
    echo 'Usage: probe-staging-public-gateway-contract.sh invariant|devtool|posture [open demo dev swagger token-mint]' >&2
    exit 64
    ;;
esac

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

expect_status 400 'OTP request validation contract' \
  --header 'Content-Type: application/json' --data '{}' \
  "${APP_ORIGIN}/v1/auth/otp/request"

if [ "$PROBE_MODE" != invariant ]; then
  roster_status=404
  if [ "$expected_open_mode" = true ] && [ "$expected_demo_users" = true ]; then
    roster_status=200
  fi
  full_roster=$(mktemp)
  trap 'rm -f -- "$full_roster"' EXIT
  full_roster_status=$(request "$full_roster" \
    "${APP_ORIGIN}/api/User/super-login/users")
  [ "$full_roster_status" = "$roster_status" ] || {
    echo "Full Super Login roster: expected HTTP ${roster_status}, received ${full_roster_status}" >&2
    exit 1
  }
  if [ "$roster_status" = 200 ]; then
    jq -e '
      (.users | type == "array")
      and ([.. | objects | has("passcode")] | any | not)
    ' "$full_roster" >/dev/null || {
      echo 'Full Super Login roster shape or no-passcode contract failed' >&2
      exit 1
    }
  fi

  expect_status "$roster_status" 'Configured demo roster' \
    "${APP_ORIGIN}/api/User/demo-users"
  dev_status=404
  [ "$expected_dev_endpoints" = false ] || dev_status=200
  expect_status "$dev_status" 'Dev Tool user directory' \
    "${APP_ORIGIN}/dev/data/users"

  mint_status=401
  if [ "$expected_open_mode" = true ] || [ "$expected_token_mint" = false ]; then
    mint_status=400
  fi
  # OpenMode=true bypasses the mint key and reaches body validation; false keeps
  # the credential gate at 401. No identity, token, passcode, or key is handled.
  expect_status "$mint_status" 'Credential-less mint posture' \
    --header 'Content-Type: application/json' --data '{}' \
    "${APP_ORIGIN}/auth/tokens"

  if [ "$PROBE_MODE" = devtool ]; then
    # The candidate smoke later proves enabled/admin access. Here, the public
    # contract proves that the enabled surface remains concealed anonymously.
    expect_status 404 'Anonymous Swagger UI concealment' \
      "${APP_ORIGIN}/swagger/index.html"
    expect_status 404 'Anonymous Swagger document concealment' \
      "${APP_ORIGIN}/swagger/v1/swagger.json"
  else
    # A recovered incumbent may predate the 404-concealment middleware and return
    # 401 while still matching its captured Spec exactly. Do not turn that old
    # behavior into a false recovery failure; Swagger posture is proven by the
    # exact full-Spec comparison surrounding this read-only public probe.
    : "$expected_swagger"
  fi
fi

echo "Staging public origin, CSRF, OTP, and ${PROBE_MODE} contracts are exact."
