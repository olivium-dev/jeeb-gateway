#!/usr/bin/env bash
# G-22 gate (PRE-03 + PRE-06): registry shape, one-way repo-subset-of-registry containment,
# forbidden names inactive. Rationale + inventory method (pending D1): docs/runbooks/gwdbx-program-rules.md.
set -euo pipefail

SRC="src/JeebGateway"
FLAGS_CS="$SRC/Services/UpstreamFeatureFlags.cs"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="$SCRIPT_DIR/gwdbx-flag-registry.txt"
fail=0

# Framework enum TYPE names, not config flags — reviewed on every addition to
# this list (pending owner decision D1).
FRAMEWORK_MODE_TYPES="BoundedChannelFullMode
FailMode
FullMode
OpenMode
PushDeliveryMode
SameSiteMode"

echo "== G-22 gate: gwdbx flag registry =="

if ! command -v jq >/dev/null 2>&1; then
  echo "FAIL: jq not found — G-22 requires jq path-flattening for the appsettings"
  echo "      key inventory (colon-grep is not an accepted substitute)."
  exit 1
fi
if [ ! -f "$REGISTRY" ]; then
  echo "FAIL: registry not found at $REGISTRY"
  exit 1
fi
if [ ! -f "$FLAGS_CS" ]; then
  echo "FAIL: $FLAGS_CS not found — run from the repository root."
  exit 1
fi

ROWS="$(grep -vE '^[[:space:]]*(#|$)' "$REGISTRY" | sed 's/[[:space:]]*#.*$//')"

# ---- Invariant (1): registry shape + one cutover per flag -------------------
echo "-- (1) registry shape + G-22 one-cutover-per-flag"
shape_errors="$(printf '%s\n' "$ROWS" | awk '
  NF != 3 { printf "  malformed row (want 3 fields): %s\n", $0; next }
  $2 != "baseline" && $2 != "program" && $2 != "forbidden" {
    printf "  unknown status %s: %s\n", $2, $1; next }
  ($2 == "baseline" || $2 == "forbidden") && $3 != "-" {
    printf "  %s row must carry owning-wave \"-\": %s\n", $2, $1 }
  $2 == "program" {
    if ($3 !~ /^create=[A-Za-z0-9-]+;delete=[A-Za-z0-9-]+$/)
      printf "  program row needs create=<wave>;delete=<wave>: %s\n", $1
    else {
      split($3, p, ";"); sub(/^create=/, "", p[1])
      if (p[1] ~ /[,+ ]/ || p[1] ~ /W[0-9].*W[0-9]/)
        printf "  flag gates two cutovers (G-22): %s -> %s\n", $1, p[1]
    }
  }')"
dupes="$(printf '%s\n' "$ROWS" | awk '{print $1}' | sort | uniq -d || true)"
if [ -n "$dupes" ]; then
  shape_errors="$shape_errors
$(printf '%s\n' "$dupes" | sed 's/^/  duplicate token (a flag may gate one cutover only): /')"
fi
if [ -n "${shape_errors//[[:space:]]/}" ]; then
  echo "FAIL: registry entries are not well-formed:"
  printf '%s\n' "$shape_errors" | grep -vE '^[[:space:]]*$'
  fail=1
else
  echo "OK: $(printf '%s\n' "$ROWS" | grep -cE '.') entries, unique tokens, one cutover each."
fi

APPROVED="$(printf '%s\n' "$ROWS" | awk '$2 == "baseline" || $2 == "program" {print $1}' | sort -u)"
FORBIDDEN="$(printf '%s\n' "$ROWS" | awk '$2 == "forbidden" {print $1}' | sort -u)"

# Inventory: jq-flattened appsettings keys (authoritative) + the *Mode arm.
# `paths(scalars)` is WRONG here — it drops `false` values, hiding default-OFF flags.
RAW_CONFIG_KEYS="$(for f in "$SRC"/appsettings*.json; do
    jq -r 'paths as $p | select(getpath($p) | type | . != "object" and . != "array") | $p | join(":")' "$f"
  done | grep -vE '(^|:)_comment' | sort -u)"
ALL_CONFIG_KEYS="$(printf '%s\n' "$RAW_CONFIG_KEYS" | sed 's/^FeatureFlags://' | sort -u)"
CONFIG_FLAGS="$(printf '%s\n' "$RAW_CONFIG_KEYS" | grep -E '^FeatureFlags:|Migration:|Mode$' \
  | sed 's/^FeatureFlags://' | sort -u || true)"
CODE_FLAGS="$(grep -E '^[[:space:]]*public[[:space:]]+bool[[:space:]]+[A-Za-z0-9_]+' "$FLAGS_CS" \
  | sed -E 's/.*public[[:space:]]+bool[[:space:]]+([A-Za-z0-9_]+).*/UseUpstream:\1/' | sort -u)"
MODE_TOKENS="$(git grep -howE '[A-Za-z]+Mode' -- "$SRC" | sort -u \
  | grep -vxF "$FRAMEWORK_MODE_TYPES" || true)"
INVENTORY="$(printf '%s\n%s\n%s\n' "$CONFIG_FLAGS" "$CODE_FLAGS" "$MODE_TOKENS" \
  | grep -vE '^[[:space:]]*$' | sort -u)"

# ---- Invariant (2): one-way containment (repo subset of registry) -----------
echo "-- (2) one-way containment: repo flag tokens subset of the registry"
# Forbidden tokens are reported by invariant (3), which owns the remedy.
unlisted="$(comm -23 <(printf '%s\n' "$INVENTORY") <(printf '%s\n' "$APPROVED") \
  | comm -23 - <(printf '%s\n' "$FORBIDDEN") || true)"
if [ -n "$unlisted" ]; then
  echo "FAIL: flag token(s) present in the repo but not approved in the registry:"
  printf '  %s\n' $unlisted
  echo "      Add the entry to $REGISTRY and to the playbook's Flag registry (A10)"
  echo "      table in the same PR, or delete the flag."
  fail=1
else
  echo "OK: $(printf '%s\n' "$INVENTORY" | grep -cE '.') repo token(s) inventoried, none outside the registry."
fi

# ---- Invariant (3): forbidden names are never active config keys ------------
echo "-- (3) forbidden names absent from active config keys and UpstreamFeatureFlags"
active_forbidden="$(comm -12 <(printf '%s\n' "$FORBIDDEN") \
  <(printf '%s\n%s\n' "$ALL_CONFIG_KEYS" "$CODE_FLAGS" | sort -u) || true)"
if [ -n "$active_forbidden" ]; then
  echo "FAIL: SUPERSEDED/retired name(s) active as a config key or a bound flag property:"
  printf '  %s\n' $active_forbidden
  echo "      These carry SUPERSEDED stamps (A2/A3/A13/F1) or are UPG-retired (G-05)"
  echo "      and must never be re-added; comment/doc mentions are allowed."
  fail=1
else
  echo "OK: $(printf '%s\n' "$FORBIDDEN" | grep -cE '.') forbidden name(s), 0 active (comment/doc mentions ignored)."
fi

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "G-22 gate FAILED — flag registry violated. See PLAN/PLAYBOOK §5 A10."
  exit 1
fi
echo ""
echo "G-22 gate PASSED — repo flags are contained by the registry."
