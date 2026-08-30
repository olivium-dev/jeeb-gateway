#!/usr/bin/env bash
set -euo pipefail

# The OTP cutover intentionally replaces the frozen provider path. The owner-selected,
# protected workflow mode is the authorization to skip the pre-cutover proof. Every
# later caller proves that an empty verification request reaches the post-cutover
# gateway and still fails closed without creating a session.
if [ "${DEPLOYMENT_MODE:-}" = otp-cutover ]; then
  printf '%s\n' 'OTP verification freeze proof skipped for protected otp-cutover mode.'
  exit 0
fi

readonly STAGING_GATEWAY_ORIGIN=https://app.jeeb.fds-1.com
readonly PROBE_USER_AGENT=Jeeb-Staging-Deploy/1.0

fail() {
  printf 'RED: staging OTP verification fail-closed proof failed (%s)\n' "$1" >&2
  exit 1
}

header_value() {
  local header_file=$1 expected_name=$2
  awk -F':' -v expected_name="$expected_name" '
    /^HTTP\// { delete values; count=0; next }
    {
      name=tolower($1)
      if (name == expected_name) {
        value=substr($0, index($0, ":") + 1)
        sub(/^[[:space:]]+/, "", value)
        sub(/\r$/, "", value)
        values[++count]=value
      }
    }
    END {
      if (count != 1) exit 1
      print values[1]
    }
  ' "$header_file"
}

header_absent() {
  local header_file=$1 rejected_name=$2
  awk -F':' -v rejected_name="$rejected_name" '
    /^HTTP\// { count=0; next }
    tolower($1) == rejected_name { count++ }
    END { exit !(count == 0) }
  ' "$header_file"
}

nonce=${STAGING_OTP_FREEZE_NONCE:-}
if [ -z "$nonce" ]; then
  nonce=$(openssl rand -hex 16 2>/dev/null) || fail nonce-generation
fi
[[ "$nonce" =~ ^[0-9a-f]{32}$ ]] || fail nonce-format

evidence_root=$(mktemp -d)
chmod 700 "$evidence_root"
trap 'status=$?; rm -rf -- "$evidence_root"; exit "$status"' EXIT
targets=(
  '/v1/auth/otp/verify'
  '/v1/auth/otp/verify/'
  '/auth/otp/verify'
  '/auth/otp/verify/'
)

for index in "${!targets[@]}"; do
  response_body="$evidence_root/body-${index}"
  response_headers="$evidence_root/headers-${index}"
  expected_body="$evidence_root/expected-${index}"
  chmod 600 "$response_body" "$response_headers" 2>/dev/null || true
  printf \
    '{"type":"https://problems.jeeb.lb/auth/invalid_otp","title":"Invalid code","status":401,"detail":"The OTP code is missing or empty.","instance":"%s"}' \
    "${targets[$index]}" > "$expected_body"
  chmod 600 "$expected_body"
  if ! status=$(curl --silent --connect-timeout 10 --max-time 20 \
      --proto '=https' --tlsv1.2 --request POST \
      --header 'Accept: application/problem+json' \
      --header "User-Agent: $PROBE_USER_AGENT" \
      --output "$response_body" --dump-header "$response_headers" \
      --write-out '%{http_code}' \
      "${STAGING_GATEWAY_ORIGIN}${targets[$index]}?cutover_nonce=${nonce}" \
      2>/dev/null); then
    fail transport
  fi
  [ "$status" = 401 ] || fail status

  content_type=$(header_value "$response_headers" content-type) || fail content-type
  media_type=${content_type%%;*}
  [ "$(printf '%s' "$media_type" | tr '[:upper:]' '[:lower:]')" = \
    application/problem+json ] || fail content-type
  header_absent "$response_headers" retry-after || fail retry-after
  header_absent "$response_headers" set-cookie || fail set-cookie
  header_absent "$response_headers" authorization || fail authorization
  cmp -s "$expected_body" "$response_body" || fail response-body
  jq -e --arg instance "${targets[$index]}" '
    type == "object"
    and keys == ["detail", "instance", "status", "title", "type"]
    and .type == "https://problems.jeeb.lb/auth/invalid_otp"
    and .title == "Invalid code"
    and .status == 401
    and .detail == "The OTP code is missing or empty."
    and .instance == $instance
    and has("code") == false
    and has("errorCode") == false
    and has("retryAfter") == false
  ' "$response_body" >/dev/null || fail problem-contract
done

printf '%s\n' 'Staging OTP verification fail-closed proof passed (4 public HTTPS probes; response bodies suppressed).'
