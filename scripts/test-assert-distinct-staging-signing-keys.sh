#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
contract="$repository_root/scripts/assert-distinct-staging-signing-keys.sh"

values=(
  "$(printf 'j%.0s' {1..64})"
  "$(printf 'u%.0s' {1..64})"
  "$(printf 'g%.0s' {1..64})"
  "$(printf 'm%.0s' {1..64})"
  "$(printf 'p%.0s' {1..64})"
)

run_contract() {
  JWT_SIGNING_KEY=$1 \
  UMJWT_SIGNING_KEY=$2 \
  JEEB_RTC_GUARDIAN_SECRET_KEY=$3 \
  JEEB_RTC_MEMBERSHIP_TICKET_KEY=$4 \
  JEEB_STAGING_WSS_PROBE_MINT_KEY=$5 \
    bash "$contract" >/dev/null 2>&1
}

run_contract "${values[@]}"
run_contract "${values[0]}" '' "${values[2]}" "${values[3]}" "${values[4]}"

collisions=0
for ((left = 0; left < ${#values[@]}; left += 1)); do
  for ((right = left + 1; right < ${#values[@]}; right += 1)); do
    mutated=("${values[@]}")
    mutated[$right]=${mutated[$left]}
    if run_contract "${mutated[@]}"; then
      echo "signing-key contract accepted collision $left:$right" >&2
      exit 1
    fi
    collisions=$((collisions + 1))
  done
done
[ "$collisions" -eq 10 ]

if run_contract short "${values[1]}" "${values[2]}" "${values[3]}" "${values[4]}"; then
  echo 'signing-key contract accepted a short JWT key' >&2
  exit 1
fi

echo 'Staging signing-key collision tests PASSED (10 pairs + length control)'
