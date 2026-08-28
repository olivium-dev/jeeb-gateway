#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 7 ] || exit 64
deployment_mode=$1
final_spec=$2
final_version=$3
final_id=$4
candidate_spec=$5
candidate_version=$6
candidate_id=$7

case "$deployment_mode" in
  normal|security-cutover|otp-cutover|devtool-reassert) ;;
  *) exit 64 ;;
esac

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=staging-gateway-spec-canonicalization.sh disable=SC1091
source "$script_root/staging-gateway-spec-canonicalization.sh"

staging_gateway_specs_equal "$final_spec" "$candidate_spec" || exit 1
cmp -s "$final_id" "$candidate_id" || exit 1

if [ "$deployment_mode" != devtool-reassert ]; then
  cmp -s "$final_version" "$candidate_version" || exit 1
  exit 0
fi

# Dev Tool smoke exercises mutable staging APIs and may overlap a semantically
# identical service-spec reassertion. Docker advances Version.Index for that
# no-op submission even though the complete Spec, immutable digest, and service
# identity remain exact. Accept only that monotonic counter advance here.
final_version_index=$(<"$final_version")
candidate_version_index=$(<"$candidate_version")
[[ "$final_version_index" =~ ^[0-9]+$ ]] || exit 1
[[ "$candidate_version_index" =~ ^[0-9]+$ ]] || exit 1
[ "$final_version_index" -ge "$candidate_version_index" ]
