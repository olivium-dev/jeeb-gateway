#!/usr/bin/env bash
# qa/t-be-004/i18n-key-check.sh — AC7 CI gate.
#
# Walks every flavors/jeeb_*/*.json template and asserts:
#   1) every declared field has i18n_label_key matching
#        ^kyc\.jeeb\.v1\.[a-z_]+\.(label|helper|placeholder|error\.<rule>)$
#   2) no literal string appears in label / helperText / placeholder anywhere
#      (each MUST be an ARB key reference)
#
# Spec authority: LEAD comment 14763 on JEB-40, AC7.
#
# Exit codes:
#   0 — all flavors compliant
#   1 — at least one violation found
#   2 — missing dependency (jq) or no flavors discovered
#
# Usage:
#   bash qa/t-be-004/i18n-key-check.sh                 # walks flavors/jeeb_*
#   bash qa/t-be-004/i18n-key-check.sh path/to/x.json  # specific file(s)

set -euo pipefail

if ! command -v jq >/dev/null 2>&1; then
  echo "ERR: jq not installed (required for AC7 i18n gate)" >&2
  exit 2
fi

KEY_RE='^kyc\.jeeb\.v1\.[a-z_]+\.(label|helper|placeholder|error\.(required|format|minLength|maxLength|fileSize|mimeType|minDate|enum))$'

# Flavors live alongside this gate in the Jeeb product repo (jeeb-gateway):
#   product/form-builder/flavors/jeeb_jeeber_v1/*.json
# Resolve relative to this script so the gate is location-independent.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FLAVORS_DIR="${FORM_BUILDER_FLAVORS_DIR:-$SCRIPT_DIR/../flavors}"

FILES=()
if [ "$#" -gt 0 ]; then
  FILES=( "$@" )
else
  # find every JSON under the product flavors dir whose path contains "jeeb_"
  # (portable for bash 3.2 on macOS — no mapfile)
  while IFS= read -r line; do
    FILES+=( "$line" )
  done < <(find "$FLAVORS_DIR" -type f -name '*.json' -path '*jeeb_*' | sort)
fi

if [ "${#FILES[@]}" -eq 0 ]; then
  echo "ERR: no Jeeb flavor JSON files discovered under flavors/" >&2
  exit 2
fi

fail=0
for f in "${FILES[@]}"; do
  echo "→ $f"

  # 1) every field must carry i18n_label_key matching the regex
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    fid=$(jq -r '.id // .field_id // ""' <<<"$row")
    key=$(jq -r '.i18n_label_key // ""' <<<"$row")
    if [ -z "$key" ]; then
      echo "  X field '$fid' missing i18n_label_key" >&2
      fail=1
    elif ! [[ "$key" =~ $KEY_RE ]]; then
      echo "  X field '$fid' i18n_label_key '$key' does not match $KEY_RE" >&2
      fail=1
    fi
  done < <(
    jq -c '
      if (.fields | type) == "array" then
        .fields[]
      elif (.fields | type) == "object" then
        .fields | to_entries[] | (.value + {id: .key})
      else
        empty
      end
    ' "$f"
  )

  # 2) no literal label / helperText / placeholder strings anywhere
  while IFS= read -r leaf; do
    [ -z "$leaf" ] && continue
    path=$(jq -r '.path' <<<"$leaf")
    val=$(jq -r '.value' <<<"$leaf")
    if ! [[ "$val" =~ $KEY_RE ]]; then
      echo "  X literal copy at $path = \"$val\" (must reference an ARB key)" >&2
      fail=1
    fi
  done < <(
    jq -c '
      [ paths(scalars) as $p
        | select($p[-1] | IN("label","helperText","placeholder"))
        | { path: ($p | map(tostring) | join(".")), value: getpath($p) }
        | select(.value | type == "string")
      ] | .[]
    ' "$f"
  )
done

if [ "$fail" -ne 0 ]; then
  echo
  echo "AC7 i18n gate FAILED. See violations above." >&2
  exit 1
fi
echo "AC7 i18n gate PASSED for ${#FILES[@]} file(s)."
