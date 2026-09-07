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
      # Read each input once: callers may supply a process-substitution stream.
      def valid:
        type == "object" and (.paths | type == "object" and length > 0) and
        (.paths | to_entries | all(.value | type == "object")) and
        ([.paths[] | keys[] | select(. as $key | $methods | index($key))] | length > 0) and
        (.paths | to_entries | all(.value | to_entries | all(
          if (.key as $key | $methods | index($key)) != null then
            (.value | type == "object") and
            (.value.responses | type == "object" and length > 0) and
            (.value.responses | keys | any(startswith("x-") | not)) and
            (.value.responses | to_entries | all(
              if .key | startswith("x-") then true else
                (.key == "default" or (.key | test("^[1-5]([0-9]{2}|XX)$"))) and
                (.value | type == "object") and
                ((.value.description | type == "string") or
                 (.value["$ref"] | type == "string" and length > 0))
              end
            ))
          else true end
        )));
      if ($base | length) != 1 or ($candidate | length) != 1 or
         ($base[0] | valid | not) or ($candidate[0] | valid | not) then
        error("Invalid OpenAPI path/operation shape")
      else
      $base[0].paths
      | to_entries[]
      | .key as $path
      | .value
      | keys[]
      | select(. as $method | $methods | index($method))
      | select($candidate[0].paths[$path][.] == null)
      | "\(.) \($path)"
      end
    '
)

if [[ -n "$missing" ]]; then
  echo "::error::OpenAPI candidate removes existing path/method contracts:" >&2
  echo "$missing" >&2
  exit 1
fi

echo "OK: every base OpenAPI path/method remains present."
