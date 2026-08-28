#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
contract="$repository_root/scripts/staging-gateway-incumbent-devtool-posture.jq"
test_root=$(mktemp -d)
trap 'rm -f -- "$test_root"/*.json; rmdir -- "$test_root"' EXIT
empty="$test_root/empty.json"
explicit="$test_root/explicit.json"
mutant="$test_root/mutant.json"

printf '%s\n' '{"TaskTemplate":{"ContainerSpec":{"Env":[]}}}' > "$empty"
[ "$(jq -er -f "$contract" "$empty")" = $'false\ttrue\tfalse\tfalse\ttrue' ] || {
  echo 'incumbent posture defaults do not match runtime option defaults' >&2
  exit 1
}

jq '.TaskTemplate.ContainerSpec.Env = [
  "SUPERlogin__OPENmode=TRUE",
  "DemoUsers__Enabled=False",
  "Features__DevEndpoints__Enabled=tRuE",
  "Features__Swagger__Enabled=TrUe",
  "Security__TokenMint__Enabled=FALSE"
]' "$empty" > "$explicit"
[ "$(jq -er -f "$contract" "$explicit")" = $'true\tfalse\ttrue\ttrue\tfalse' ] || {
  echo 'incumbent posture explicit values were not derived exactly' >&2
  exit 1
}

for unsafe_filter in \
  '.TaskTemplate.ContainerSpec.Env += ["DEMOusers__enabled=true"]' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "SuperLogin__OpenMode=yes"' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "SuperLogin__OpenMode=1"' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "SuperLogin__OpenMode="' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "SuperLogin__OpenMode= true"'; do
  jq "$unsafe_filter" "$explicit" > "$mutant"
  if jq -e -f "$contract" "$mutant" >/dev/null 2>&1; then
    echo "incumbent posture accepted ambiguous input: $unsafe_filter" >&2
    exit 1
  fi
done

echo 'staging incumbent Dev Tool posture derivation: PASS (runtime defaults, case-insensitive values, 5 negative controls)'
