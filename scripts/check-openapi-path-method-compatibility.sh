#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 BASE_OPENAPI CANDIDATE_OPENAPI" >&2
  exit 64
fi

base=$1
candidate=$2
methods='["get","put","post","delete","options","head","patch","trace"]'

missing=$(
  jq -n -r --slurpfile base "$base" --slurpfile candidate "$candidate" \
    --argjson methods "$methods" '
      $base[0].paths
      | to_entries[]
      | .key as $path
      | .value
      | keys[]
      | select(. as $method | $methods | index($method))
      | select($candidate[0].paths[$path][.] == null)
      | "\(.) \($path)"
    '
)

if [[ -n "$missing" ]]; then
  echo "::error::OpenAPI candidate removes existing path/method contracts:" >&2
  echo "$missing" >&2
  exit 1
fi

echo "OK: every base OpenAPI path/method remains present."
