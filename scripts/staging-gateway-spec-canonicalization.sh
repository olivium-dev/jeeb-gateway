#!/usr/bin/env bash

# Docker service Specs are JSON objects. Canonicalization sorts object keys and
# removes insignificant whitespace while retaining every field and array order.

staging_gateway_canonicalize_spec_file() {
  local source=$1 destination=${2:-$1}
  local temporary
  [ -s "$source" ] || return 1
  temporary=$(mktemp "${destination}.canonical.XXXXXX") || return 1
  chmod 600 "$temporary"
  if jq -e -S -c -s \
      'if length == 1 and (.[0] | type == "object") then .[0]
       else error("service Spec must be exactly one JSON object") end' \
      "$source" > "$temporary" \
    && [ -s "$temporary" ]; then
    mv -f -- "$temporary" "$destination"
    chmod 600 "$destination"
    return 0
  fi
  rm -f -- "$temporary"
  return 1
}

staging_gateway_specs_equal() {
  local left=$1 right=$2 comparison_root canonical_left canonical_right status=1
  [ -s "$left" ] && [ -s "$right" ] || return 1
  comparison_root=$(mktemp -d) || return 1
  chmod 700 "$comparison_root"
  canonical_left="$comparison_root/left.json"
  canonical_right="$comparison_root/right.json"
  if staging_gateway_canonicalize_spec_file "$left" "$canonical_left" \
    && staging_gateway_canonicalize_spec_file "$right" "$canonical_right" \
    && cmp -s "$canonical_left" "$canonical_right"; then
    status=0
  fi
  rm -f -- "$canonical_left" "$canonical_right"
  rmdir -- "$comparison_root"
  return "$status"
}
