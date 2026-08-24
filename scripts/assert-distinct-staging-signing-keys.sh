#!/usr/bin/env bash
set -euo pipefail

: "${JWT_SIGNING_KEY:?JEEB_JWT_SIGNING_KEY is required}"
: "${JEEB_RTC_GUARDIAN_SECRET_KEY:?JEEB_RTC_GUARDIAN_SECRET_KEY is required}"
: "${JEEB_RTC_MEMBERSHIP_TICKET_KEY:?JEEB_RTC_MEMBERSHIP_TICKET_KEY is required}"
: "${JEEB_STAGING_WSS_PROBE_MINT_KEY:?JEEB_STAGING_WSS_PROBE_MINT_KEY is required}"

byte_length() {
  printf '%s' "$1" | LC_ALL=C wc -c | tr -d ' '
}

[ "$(byte_length "$JWT_SIGNING_KEY")" -ge 32 ]
[ -z "${UMJWT_SIGNING_KEY:-}" ] || \
  [ "$(byte_length "$UMJWT_SIGNING_KEY")" -ge 32 ]
[ "$(byte_length "$JEEB_RTC_GUARDIAN_SECRET_KEY")" -ge 64 ]
[ "$(byte_length "$JEEB_RTC_MEMBERSHIP_TICKET_KEY")" -ge 32 ]
[ "$(byte_length "$JEEB_STAGING_WSS_PROBE_MINT_KEY")" -ge 32 ]

key_names=(jwt guardian membership probe)
key_values=(
  "$JWT_SIGNING_KEY"
  "$JEEB_RTC_GUARDIAN_SECRET_KEY"
  "$JEEB_RTC_MEMBERSHIP_TICKET_KEY"
  "$JEEB_STAGING_WSS_PROBE_MINT_KEY"
)
if [ -n "${UMJWT_SIGNING_KEY:-}" ]; then
  key_names+=(umjwt)
  key_values+=("$UMJWT_SIGNING_KEY")
fi

for ((left = 0; left < ${#key_values[@]}; left += 1)); do
  for ((right = left + 1; right < ${#key_values[@]}; right += 1)); do
    if [ "${key_values[$left]}" = "${key_values[$right]}" ]; then
      printf 'staging signing authorities must be distinct: %s collides with %s\n' \
        "${key_names[$left]}" "${key_names[$right]}" >&2
      exit 1
    fi
  done
done
