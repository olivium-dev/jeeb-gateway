#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 BASE_OPENAPI CURRENT_OPENAPI OUTPUT_OPENAPI" >&2
  exit 64
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
output=$3
tmp=$(mktemp "${output}.tmp.XXXXXX")
trap 'rm -f -- "$tmp"' EXIT

jq -s -f "$script_dir/update-wallet-openapi-slice.jq" "$1" "$2" > "$tmp"
# System.Text.Json escapes '+' in wildcard media-type keys. Preserve that stable
# representation so a narrow semantic slice does not churn unrelated operations.
perl -pi -e 's#\+json#\\u002Bjson#g' "$tmp"
mv -- "$tmp" "$output"
trap - EXIT
