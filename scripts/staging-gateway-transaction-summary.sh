#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 7 ] || exit 64
incumbent_spec=$1
incumbent_manifest=$2
candidate_spec=$3
candidate_manifest=$4
forward_result=$5
recovery_result=$6
summary=$7

read_identity() {
  local prefix=$1 spec=$2 manifest=$3
  local spec_hash image digest service_id version manifest_hash
  if [ ! -s "$spec" ] && [ ! -s "$manifest" ]; then
    return 0
  fi
  [ -s "$spec" ] && [ -s "$manifest" ]
  spec_hash=$(sha256sum "$spec" | awk '{print $1}')
  IFS=$'\t' read -r image digest service_id version manifest_hash < <(
    jq -er '[.Image,.ImageDigest,.ServiceID,(.VersionIndex | tostring),.SpecSha256] | @tsv' \
      "$manifest"
  )
  [[ "$spec_hash" =~ ^[0-9a-f]{64}$ ]]
  [[ "$image" =~ @sha256:[0-9a-f]{64}$ ]]
  [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]
  [[ "$service_id" =~ ^[a-z0-9]+$ ]]
  [[ "$version" =~ ^[0-9]+$ ]]
  [ "$manifest_hash" = "$spec_hash" ]
  printf -v "${prefix}_hash" '%s' "$spec_hash"
  printf -v "${prefix}_image" '%s' "$image"
  printf -v "${prefix}_digest" '%s' "$digest"
  printf -v "${prefix}_service_id" '%s' "$service_id"
  printf -v "${prefix}_version" '%s' "$version"
}

incumbent_hash=''; incumbent_image=''; incumbent_digest=''
incumbent_service_id=''; incumbent_version=''
candidate_hash=''; candidate_image=''; candidate_digest=''
candidate_service_id=''; candidate_version=''
read_identity incumbent "$incumbent_spec" "$incumbent_manifest"
read_identity candidate "$candidate_spec" "$candidate_manifest"

forward=not-submitted
[ ! -s "$forward_result" ] || forward=$(<"$forward_result")
case "$forward" in
  not-submitted|http-200-exact-candidate|http-409-exact-candidate|\
  lost-after-acceptance-exact-candidate|\
  lost-before-acceptance-bounded-retry-exact-candidate|\
  unknown-third-preserved|candidate-capture-failed-after-submit|\
  unexpected-http-status-after-submit|\
  http-200-exact-incumbent-invalid|http-409-exact-incumbent-no-retry|\
  lock-lost-before-bounded-retry|unexpected-http-status-after-bounded-retry|\
  candidate-capture-failed-after-bounded-retry|\
  bounded-retry-exact-incumbent-no-candidate|bounded-retry-unreconciled|\
  submission-interrupted-recovered-incumbent|\
  submission-interrupted-recovery-failed|security-cutover-submitted-pending|\
  security-cutover-http-200-exact-candidate|\
  security-cutover-cas-rejected-fix-forward|\
  security-cutover-ambiguous-fix-forward|\
  security-cutover-exact-state-unavailable|\
  security-cutover-unknown-state-fix-forward|\
  security-cutover-interrupted-fix-forward) ;;
  *) forward=invalid-result-refused ;;
esac
case "$recovery_result" in
  not-armed|armed-pending|not-required|exact-incumbent-recovered|recovery-failed) ;;
  *) recovery_result=invalid-result-refused ;;
esac

{
  printf '%s\n' '### Staging gateway Spec transaction'
  if [ -n "$incumbent_hash" ]; then
    printf -- '- incumbent_spec_sha256: %s\n' "$incumbent_hash"
    printf -- '- incumbent_image: %s\n' "$incumbent_image"
    printf -- '- incumbent_image_digest: %s\n' "$incumbent_digest"
    printf -- '- incumbent_service_id: %s\n' "$incumbent_service_id"
    printf -- '- incumbent_version_index: %s\n' "$incumbent_version"
  fi
  if [ -n "$candidate_hash" ]; then
    printf -- '- candidate_spec_sha256: %s\n' "$candidate_hash"
    printf -- '- candidate_image: %s\n' "$candidate_image"
    printf -- '- candidate_image_digest: %s\n' "$candidate_digest"
    printf -- '- candidate_service_id: %s\n' "$candidate_service_id"
    printf -- '- candidate_version_index: %s\n' "$candidate_version"
  fi
  printf -- '- forward_reconciliation: %s\n' "$forward"
  printf -- '- recovery_result: %s\n' "$recovery_result"
} >> "$summary"
