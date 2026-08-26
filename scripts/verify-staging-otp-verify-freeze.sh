#!/usr/bin/env bash
set -euo pipefail

readonly STAGING_GATEWAY_ORIGIN=https://app.jeeb.fds-1.com
readonly EXPECTED_PROBLEM='{"type":"about:blank","title":"Service Unavailable","status":503,"detail":"The service is temporarily unavailable. Please try again."}'

fail() {
  printf 'RED: staging OTP verification freeze proof failed (%s)\n' "$1" >&2
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
expected_body="$evidence_root/expected"
printf '%s' "$EXPECTED_PROBLEM" > "$expected_body"
chmod 600 "$expected_body"

targets=(
  '/v1/auth/otp/verify'
  '/v1/auth/otp/verify/'
  '/auth/otp/verify'
  '/auth/otp/verify/'
)

for index in "${!targets[@]}"; do
  response_body="$evidence_root/body-${index}"
  response_headers="$evidence_root/headers-${index}"
  chmod 600 "$response_body" "$response_headers" 2>/dev/null || true
  if ! status=$(curl --silent --connect-timeout 10 --max-time 20 \
      --proto '=https' --tlsv1.2 --request POST \
      --header 'Accept: application/problem+json' \
      --output "$response_body" --dump-header "$response_headers" \
      --write-out '%{http_code}' \
      "${STAGING_GATEWAY_ORIGIN}${targets[$index]}?cutover_nonce=${nonce}" \
      2>/dev/null); then
    fail transport
  fi
  [ "$status" = 503 ] || fail status

  content_type=$(header_value "$response_headers" content-type) || fail content-type
  [ "$(printf '%s' "$content_type" | tr '[:upper:]' '[:lower:]')" = \
    application/problem+json ] || fail content-type
  cache_control=$(header_value "$response_headers" cache-control) || fail cache-control
  [ "$(printf '%s' "$cache_control" | tr '[:upper:]' '[:lower:]')" = no-store ] \
    || fail cache-control
  header_absent "$response_headers" retry-after || fail retry-after
  cmp -s "$expected_body" "$response_body" || fail response-body
  jq -e '
    type == "object"
    and keys == ["detail", "status", "title", "type"]
    and .type == "about:blank"
    and .title == "Service Unavailable"
    and .status == 503
    and .detail == "The service is temporarily unavailable. Please try again."
    and has("instance") == false
    and has("code") == false
    and has("errorCode") == false
    and has("retryAfter") == false
  ' "$response_body" >/dev/null || fail problem-contract
done

printf '%s\n' 'Staging OTP verification freeze proof passed (4 public HTTPS probes; response bodies suppressed).'
