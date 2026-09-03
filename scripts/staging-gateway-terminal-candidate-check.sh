#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "RED: terminal candidate check failed: $*" >&2
  exit 1
}

if [ "$#" -ne 7 ]; then
  echo "RED: terminal candidate check usage: expected 7 arguments, got $#" >&2
  exit 64
fi
deployment_mode=$1
final_spec=$2
final_version=$3
final_id=$4
candidate_spec=$5
candidate_version=$6
candidate_id=$7

case "$deployment_mode" in
  normal|security-cutover|otp-cutover|devtool-reassert) ;;
  *)
    echo "RED: terminal candidate check: unsupported deployment mode '$deployment_mode'" >&2
    exit 64
    ;;
esac

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=staging-gateway-spec-canonicalization.sh disable=SC1091
source "$script_root/staging-gateway-spec-canonicalization.sh"

# Specs carry the service environment, so identity is reported as a canonical
# digest. An unreadable Spec reports the empty digest and still fails closed.
spec_digest() {
  local source=$1 canonical
  canonical=$(mktemp) || return 0
  if staging_gateway_canonicalize_spec_file "$source" "$canonical"; then
    sha256sum < "$canonical" | awk '{print $1}'
  fi
  rm -f -- "$canonical"
}

staging_gateway_specs_equal "$final_spec" "$candidate_spec" || fail \
  "final Spec is not the submitted candidate Spec" \
  "(final sha256=$(spec_digest "$final_spec") candidate sha256=$(spec_digest "$candidate_spec"))"
cmp -s "$final_id" "$candidate_id" || fail \
  "Service.ID drifted (final='$(tr -d '\n' < "$final_id")'" \
  "candidate='$(tr -d '\n' < "$candidate_id")')"

final_version_index=$(<"$final_version")
candidate_version_index=$(<"$candidate_version")
[[ "$final_version_index" =~ ^[0-9]+$ ]] \
  || fail "final Version.Index is malformed (final='$final_version_index')"
[[ "$candidate_version_index" =~ ^[0-9]+$ ]] \
  || fail "candidate Version.Index is malformed (candidate='$candidate_version_index')"

case "$deployment_mode" in
  security-cutover)
    # Only this mode pins candidate_version_index into a separate remote CAS
    # proof, so it keeps the exact compare until that proof is re-reviewed.
    [ "$final_version_index" = "$candidate_version_index" ] || fail \
      "$deployment_mode requires an exact Version.Index" \
      "(final=$final_version_index candidate=$candidate_version_index)"
    ;;
  *)
    # Version.Index is a monotonic counter, not an identity: Swarm writes
    # UpdateStatus after convergence, so it always advances past the submit.
    [ "$final_version_index" -ge "$candidate_version_index" ] || fail \
      "Version.Index went backwards" \
      "(final=$final_version_index candidate=$candidate_version_index)"
    ;;
esac
