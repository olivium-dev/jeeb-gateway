#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# shellcheck source=staging-gateway-spec-canonicalization.sh disable=SC1091
source "$repository_root/scripts/staging-gateway-spec-canonicalization.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT

base="$test_root/base.json"
equivalent="$test_root/equivalent.json"
canonical="$test_root/canonical.json"
printf '%s\n' '{"Name":"gateway","Labels":{"b":"two","a":"one"},"Networks":[{"Target":"first"},{"Target":"second"}]}' > "$base"
printf '%s\n' '{
  "Networks": [ { "Target": "first" }, { "Target": "second" } ],
  "Labels": { "a": "one", "b": "two" },
  "Name": "gateway"
}' > "$equivalent"

staging_gateway_specs_equal "$base" "$equivalent"
staging_gateway_canonicalize_spec_file "$equivalent" "$canonical"
[ "$(wc -l < "$canonical" | tr -d ' ')" -eq 1 ]
[ "$(<"$canonical")" = '{"Labels":{"a":"one","b":"two"},"Name":"gateway","Networks":[{"Target":"first"},{"Target":"second"}]}' ]

assert_not_equal() {
  local name=$1 payload=$2 variant
  variant="$test_root/$name.json"
  printf '%s\n' "$payload" > "$variant"
  if staging_gateway_specs_equal "$base" "$variant" 2>/dev/null; then
    echo "canonical Spec comparison accepted $name" >&2
    exit 1
  fi
}

assert_not_equal nested-value-change \
  '{"Name":"gateway","Labels":{"b":"changed","a":"one"},"Networks":[{"Target":"first"},{"Target":"second"}]}'
assert_not_equal array-reorder \
  '{"Name":"gateway","Labels":{"b":"two","a":"one"},"Networks":[{"Target":"second"},{"Target":"first"}]}'
assert_not_equal null-vs-absent \
  '{"Name":"gateway","Labels":{"b":"two","a":"one"},"Networks":[{"Target":"first"},{"Target":"second"}],"EndpointSpec":null}'
assert_not_equal invalid-json '{"Name":'
assert_not_equal non-object '[{"Name":"gateway"}]'
assert_not_equal multiple-documents $'{"Name":"gateway"}\n{"Name":"gateway"}'

echo 'staging gateway Spec canonicalization: PASS (semantic equality and fail-closed rejects)'
