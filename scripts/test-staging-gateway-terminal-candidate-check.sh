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

# Swarm writes UpdateStatus after convergence, so Version.Index has always
# advanced by the time the final Spec is re-read. Run 33814328644 died here.
printf '%s\n' 41 > "$test_root/final-version"
for mode in normal devtool-reassert; do
  check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"
done

for mode in security-cutover otp-cutover; do
  if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
    echo "cutover mode accepted a higher Version.Index: $mode" >&2
    exit 1
  fi
done

printf '%s\n' 39 > "$test_root/final-version"
for mode in normal security-cutover otp-cutover devtool-reassert; do
  if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
    echo "mode accepted a lower Version.Index: $mode" >&2
    exit 1
  fi
done

for malformed in invalid '' -1; do
  printf '%s\n' "$malformed" > "$test_root/final-version"
  for mode in normal devtool-reassert; do
    if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
      echo "mode accepted a malformed Version.Index: $mode" >&2
      exit 1
    fi
  done
done

printf '%s\n' invalid > "$test_root/candidate-version"
printf '%s\n' 40 > "$test_root/final-version"
for mode in normal devtool-reassert; do
  if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/final-id"; then
    echo "mode accepted a malformed candidate Version.Index: $mode" >&2
    exit 1
  fi
done
printf '%s\n' 40 > "$test_root/candidate-version"

printf '%s\n' 41 > "$test_root/final-version"
for mode in normal devtool-reassert; do
  if check "$mode" "$test_root/changed.json" "$test_root/final-version" "$test_root/final-id"; then
    echo "mode accepted a changed service Spec: $mode" >&2
    exit 1
  fi
  if check "$mode" "$test_root/final.json" "$test_root/final-version" "$test_root/changed-id"; then
    echo "mode accepted a changed service ID: $mode" >&2
    exit 1
  fi
done

# Every rejection must name the comparison and both values; run 33814328644
# failed with no output at all and cost a whole deploy cycle to attribute.
printf '%s\n' 39 > "$test_root/final-version"
diagnostic=$(check normal "$test_root/final.json" "$test_root/final-version" \
  "$test_root/final-id" 2>&1 >/dev/null || true)
case "$diagnostic" in
  *'Version.Index went backwards'*'final=39'*'candidate=40'*) ;;
  *) echo "version rejection was not diagnostic: $diagnostic" >&2; exit 1 ;;
esac

printf '%s\n' 41 > "$test_root/final-version"
diagnostic=$(check normal "$test_root/changed.json" "$test_root/final-version" \
  "$test_root/final-id" 2>&1 >/dev/null || true)
case "$diagnostic" in
  *'not the submitted candidate Spec'*sha256=[0-9a-f]*) ;;
  *) echo "spec rejection was not diagnostic: $diagnostic" >&2; exit 1 ;;
esac
case "$diagnostic" in
  *'"Name":"other"'*|*Image*) echo 'spec rejection leaked Spec content' >&2; exit 1 ;;
esac

diagnostic=$(check normal "$test_root/final.json" "$test_root/final-version" \
  "$test_root/changed-id" 2>&1 >/dev/null || true)
case "$diagnostic" in
  *'Service.ID drifted'*other-id*service-id*) ;;
  *) echo "service ID rejection was not diagnostic: $diagnostic" >&2; exit 1 ;;
esac

diagnostic=$(bash "$subject" normal 2>&1 || true)
case "$diagnostic" in *'expected 7 arguments'*) ;;
  *) echo "arity rejection was not diagnostic: $diagnostic" >&2; exit 1 ;;
esac
diagnostic=$(check bogus-mode "$test_root/final.json" "$test_root/final-version" \
  "$test_root/final-id" 2>&1 >/dev/null || true)
case "$diagnostic" in *'unsupported deployment mode'*bogus-mode*) ;;
  *) echo "mode rejection was not diagnostic: $diagnostic" >&2; exit 1 ;;
esac

echo 'staging terminal candidate check: PASS (6 accepted, 21 adversarial, 5 diagnostic cases)'
