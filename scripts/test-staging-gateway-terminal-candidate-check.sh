#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/staging-gateway-terminal-candidate-check.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT

printf '%s\n' '{"Name":"jeeb","TaskTemplate":{"ContainerSpec":{"Image":"gateway@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}' \
  > "$test_root/candidate.json"
printf '%s\n' '{ "TaskTemplate": { "ContainerSpec": { "Image": "gateway@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } }, "Name": "jeeb" }' \
  > "$test_root/final.json"
printf '%s\n' '{"Name":"other"}' > "$test_root/changed.json"
printf '%s\n' service-id > "$test_root/candidate-id"
printf '%s\n' service-id > "$test_root/final-id"
printf '%s\n' other-id > "$test_root/changed-id"
printf '%s\n' 40 > "$test_root/candidate-version"

check() {
  local mode=$1 final_spec=$2 final_version=$3 final_id=$4
  bash "$subject" "$mode" "$final_spec" "$final_version" "$final_id" \
    "$test_root/candidate.json" "$test_root/candidate-version" "$test_root/candidate-id"
}

for mode in normal security-cutover otp-cutover devtool-reassert; do
  printf '%s\n' 40 > "$test_root/final-version"
  check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"
done

printf '%s\n' 41 > "$test_root/final-version"
check devtool-reassert "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"

for mode in normal security-cutover otp-cutover; do
  if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
    echo "non-Dev Tool mode accepted a higher Version.Index: $mode" >&2
    exit 1
  fi
done

printf '%s\n' 39 > "$test_root/final-version"
if check devtool-reassert "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
  echo 'Dev Tool mode accepted a lower Version.Index' >&2
  exit 1
fi

for malformed in invalid '' -1; do
  printf '%s\n' "$malformed" > "$test_root/final-version"
  if check devtool-reassert "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
    echo 'Dev Tool mode accepted a malformed Version.Index' >&2
    exit 1
  fi
done

printf '%s\n' invalid > "$test_root/candidate-version"
printf '%s\n' 40 > "$test_root/final-version"
if check devtool-reassert "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
  echo 'Dev Tool mode accepted a malformed candidate Version.Index' >&2
  exit 1
fi
printf '%s\n' 40 > "$test_root/candidate-version"

printf '%s\n' 40 > "$test_root/final-version"
if check devtool-reassert "$test_root/changed.json" "$test_root/final-version" "$test_root/final-id"; then
  echo 'Dev Tool mode accepted a changed service Spec' >&2
  exit 1
fi
if check devtool-reassert "$test_root/final.json" "$test_root/final-version" "$test_root/changed-id"; then
  echo 'Dev Tool mode accepted a changed service ID' >&2
  exit 1
fi

echo 'staging terminal candidate check: PASS (4 exact, 1 monotonic Dev Tool, 10 adversarial cases)'
