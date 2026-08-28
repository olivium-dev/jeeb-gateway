#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/staging-gateway-transaction-summary.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
incumbent_spec="$test_root/incumbent.json"
candidate_spec="$test_root/candidate.json"
incumbent_manifest="$test_root/incumbent-manifest.json"
candidate_manifest="$test_root/candidate-manifest.json"
forward="$test_root/forward"
summary="$test_root/summary.md"

printf '%s\n' '{"TaskTemplate":{"ContainerSpec":{"Image":"repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}' \
  > "$incumbent_spec"
printf '%s\n' '{"TaskTemplate":{"ContainerSpec":{"Image":"repo@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}' \
  > "$candidate_spec"
incumbent_hash=$(jq -e -S -c . "$incumbent_spec" | sha256sum | awk '{print $1}')
candidate_hash=$(jq -e -S -c . "$candidate_spec" | sha256sum | awk '{print $1}')
jq -n --arg hash "$incumbent_hash" '{
  Image:"repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  ImageDigest:"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  ServiceID:"serviceabc",VersionIndex:41,SpecSha256:$hash
}' > "$incumbent_manifest"
jq -n --arg hash "$candidate_hash" '{
  Image:"repo@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  ImageDigest:"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  ServiceID:"serviceabc",VersionIndex:42,SpecSha256:$hash
}' > "$candidate_manifest"
printf '%s\n' http-200-exact-candidate > "$forward"

bash "$subject" "$incumbent_spec" "$incumbent_manifest" \
  "$candidate_spec" "$candidate_manifest" "$forward" not-required "$summary"

for expected in \
  "incumbent_spec_sha256: $incumbent_hash" \
  'incumbent_image: repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' \
  'incumbent_image_digest: sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' \
  'incumbent_service_id: serviceabc' \
  'incumbent_version_index: 41' \
  "candidate_spec_sha256: $candidate_hash" \
  'candidate_image: repo@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' \
  'candidate_image_digest: sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' \
  'candidate_service_id: serviceabc' \
  'candidate_version_index: 42' \
  'forward_reconciliation: http-200-exact-candidate' \
  'recovery_result: not-required'; do
  grep -Fqx -- "- $expected" "$summary" || {
    echo "transaction summary omitted durable identity: $expected" >&2
    exit 1
  }
done
[ "$(grep -Ec '^- (incumbent|candidate)_(spec_sha256|image|image_digest|service_id|version_index):' "$summary")" -eq 10 ]
if grep -Eqi 'secret|password|token|env' "$summary"; then
  echo 'transaction summary leaked configuration material' >&2
  exit 1
fi

jq '.SpecSha256 = "bad"' "$candidate_manifest" > "$test_root/malformed.json"
if bash "$subject" "$incumbent_spec" "$incumbent_manifest" \
  "$candidate_spec" "$test_root/malformed.json" "$forward" not-required \
  "$test_root/rejected.md" >/dev/null 2>&1; then
  echo 'transaction summary accepted a malformed durable manifest' >&2
  exit 1
fi
[ ! -e "$test_root/rejected.md" ] || [ ! -s "$test_root/rejected.md" ]

# A failure before the candidate manifest is durable must not erase the
# deployment's original nonzero status while the summary remains unwritten.
partial_candidate="$test_root/partial-candidate.json"
partial_manifest="$test_root/missing-candidate-manifest.json"
partial_summary="$test_root/partial-summary.md"
cp "$candidate_spec" "$partial_candidate"
original_status=37
status=$original_status
if ! bash "$subject" "$incumbent_spec" "$incumbent_manifest" \
  "$partial_candidate" "$partial_manifest" "$forward" not-required \
  "$partial_summary" >/dev/null 2>&1; then
  [ "$status" -ne 0 ] || status=99
fi
[ "$status" -eq "$original_status" ]
[ ! -e "$partial_summary" ] || [ ! -s "$partial_summary" ]

echo 'staging gateway durable transaction summary: PASS (10 identity fields, malformed/partial state rejected)'
