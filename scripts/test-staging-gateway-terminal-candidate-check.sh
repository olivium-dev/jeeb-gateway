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

# Only security-cutover pins candidate_version_index into a remote CAS proof.
# Every other mode reaches this check through the ordinary forward apply.
all_modes=(normal security-cutover otp-cutover devtool-reassert)
monotonic_modes=(normal otp-cutover devtool-reassert)
accepted=0
adversarial=0
diagnostic=0

check() {
  local mode=$1 final_spec=$2 final_version=$3 final_id=$4
  bash "$subject" "$mode" "$final_spec" "$final_version" "$final_id" \
    "$test_root/candidate.json" "$test_root/candidate-version" "$test_root/candidate-id"
}

expect_ok() {
  local description=$1; shift
  if ! check "$@" 2>/dev/null; then
    echo "rejected a valid case: $description ($1)" >&2
    exit 1
  fi
  accepted=$((accepted + 1))
}

expect_fail() {
  local description=$1; shift
  if check "$@" 2>/dev/null; then
    echo "accepted an invalid case: $description ($1)" >&2
    exit 1
  fi
  adversarial=$((adversarial + 1))
}

expect_message() {
  local description=$1 pattern=$2; shift 2
  local observed
  observed=$(check "$@" 2>&1 >/dev/null || true)
  # shellcheck disable=SC2053
  if [[ "$observed" != $pattern ]]; then
    echo "$description was not diagnostic: $observed" >&2
    exit 1
  fi
  diagnostic=$((diagnostic + 1))
}

printf '%s\n' 40 > "$test_root/final-version"
for mode in "${all_modes[@]}"; do
  expect_ok 'equal Version.Index' "$mode" "$test_root/final.json" \
    "$test_root/final-version" "$test_root/final-id"
done

# Swarm writes UpdateStatus after convergence, so Version.Index has always
# advanced by the time the final Spec is re-read. Run 33814328644 died here.
printf '%s\n' 41 > "$test_root/final-version"
for mode in "${monotonic_modes[@]}"; do
  expect_ok 'advanced Version.Index' "$mode" "$test_root/final.json" \
    "$test_root/final-version" "$test_root/final-id"
done
expect_fail 'security-cutover higher Version.Index' security-cutover \
  "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"

printf '%s\n' 39 > "$test_root/final-version"
for mode in "${all_modes[@]}"; do
  expect_fail 'lower Version.Index' "$mode" "$test_root/final.json" \
    "$test_root/final-version" "$test_root/final-id"
done

for malformed in invalid '' -1; do
  printf '%s\n' "$malformed" > "$test_root/final-version"
  for mode in "${all_modes[@]}"; do
    expect_fail 'malformed final Version.Index' "$mode" "$test_root/final.json" \
      "$test_root/final-version" "$test_root/final-id"
  done
done

printf '%s\n' invalid > "$test_root/candidate-version"
printf '%s\n' 40 > "$test_root/final-version"
for mode in "${all_modes[@]}"; do
  expect_fail 'malformed candidate Version.Index' "$mode" "$test_root/final.json" \
    "$test_root/final-version" "$test_root/final-id"
done
printf '%s\n' 40 > "$test_root/candidate-version"

printf '%s\n' 41 > "$test_root/final-version"
for mode in "${monotonic_modes[@]}"; do
  expect_fail 'changed service Spec' "$mode" "$test_root/changed.json" \
    "$test_root/final-version" "$test_root/final-id"
  expect_fail 'changed service ID' "$mode" "$test_root/final.json" \
    "$test_root/final-version" "$test_root/changed-id"
done

# Every rejection must name the comparison and both values; run 33814328644
# failed with no output at all and cost a whole deploy cycle to attribute.
printf '%s\n' 39 > "$test_root/final-version"
expect_message 'version rejection' \
  '*Version.Index went backwards*final=39*candidate=40*' \
  normal "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"

printf '%s\n' 41 > "$test_root/final-version"
expect_message 'spec rejection' \
  '*not the submitted candidate Spec*sha256=[0-9a-f]*' \
  normal "$test_root/changed.json" "$test_root/final-version" "$test_root/final-id"
leak=$(check normal "$test_root/changed.json" "$test_root/final-version" \
  "$test_root/final-id" 2>&1 >/dev/null || true)
case "$leak" in
  *'"Name":"other"'*|*Image*) echo 'spec rejection leaked Spec content' >&2; exit 1 ;;
esac

expect_message 'service ID rejection' \
  '*Service.ID drifted*other-id*service-id*' \
  normal "$test_root/final.json" "$test_root/final-version" "$test_root/changed-id"

expect_message 'mode rejection' '*unsupported deployment mode*bogus-mode*' \
  bogus-mode "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"

arity=$(bash "$subject" normal 2>&1 || true)
case "$arity" in
  *'expected 7 arguments'*) diagnostic=$((diagnostic + 1)) ;;
  *) echo "arity rejection was not diagnostic: $arity" >&2; exit 1 ;;
esac

# Counted rather than hard-coded: the previous tally string drifted from reality.
echo "staging terminal candidate check: PASS (${accepted} accepted," \
  "${adversarial} adversarial, ${diagnostic} diagnostic cases)"
