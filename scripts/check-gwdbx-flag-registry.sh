#!/usr/bin/env bash
# G-22 gate (PRE-03 + PRE-06): registry shape, one-way repo-subset-of-registry containment,
# forbidden names inactive. Rationale + inventory method (D1/D2): docs/runbooks/gwdbx-program-rules.md.
set -euo pipefail

SRC="src/JeebGateway"
PROGRAM_CS="$SRC/Program.cs"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="$SCRIPT_DIR/gwdbx-flag-registry.txt"
fail=0

# Type names verified NOT to be configuration (BCL/ASP.NET types + one local enum);
# every addition to this list is re-verified against the binding sites (D1).
FRAMEWORK_MODE_TYPES="BoundedChannelFullMode
FileMode
FullMode
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
if [ ! -f "$PROGRAM_CS" ]; then
  echo "FAIL: $PROGRAM_CS not found — run from the repository root."
  exit 1
fi

ROWS="$(grep -vE '^[[:space:]]*(#|$)' "$REGISTRY" | sed 's/[[:space:]]*#.*$//')"

# ---- Invariant (1): registry shape + one cutover per flag -------------------
echo "-- (1) registry shape + G-22 one-cutover-per-flag"
shape_errors="$(printf '%s\n' "$ROWS" | awk '
  NF != 3 { printf "  malformed row (want 3 fields): %s\n", $0; next }
  $2 != "baseline" && $2 != "program" && $2 != "forbidden" && $2 != "setting" {
    printf "  unknown status %s: %s\n", $2, $1; next }
  ($2 == "baseline" || $2 == "forbidden" || $2 == "setting") && $3 != "-" {
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

APPROVED="$(printf '%s\n' "$ROWS" \
  | awk '$2 == "baseline" || $2 == "program" || $2 == "setting" {print $1}' | sort -u)"
FORBIDDEN="$(printf '%s\n' "$ROWS" | awk '$2 == "forbidden" {print $1}' | sort -u)"

# Inventory: jq-flattened appsettings keys + code (bound-options) arm + literal-read arm + *Mode arm.
# `paths(scalars)` is WRONG here — it drops `false` values, hiding default-OFF flags.
# Array indices collapse to the element property (Boundaries:0:Key -> Boundaries:Key).
RAW_CONFIG_KEYS="$(for f in "$SRC"/appsettings*.json; do
    jq -r 'paths as $p | select(getpath($p) | type | . != "object" and . != "array") | $p | join(":")' "$f"
  done | grep -vE '(^|:)_comment' | sed -E -e ':a' -e 's/:[0-9]+(:|$)/\1/' -e ta | sort -u)"
ALL_CONFIG_KEYS="$(printf '%s\n' "$RAW_CONFIG_KEYS" | sed 's/^FeatureFlags://' | sort -u)"
# D2: EVERY committed key, not the `FeatureFlags:|Migration:|...Mode` name-spelling guess —
# that heuristic let a committed `OpsBypass:SkipAuthentication` through untouched.
CONFIG_FLAGS="$ALL_CONFIG_KEYS"
# Code arm (D1 + D2): every options class bound ANYWHERE under $SRC — Configure<T>(…GetSection(…)),
# AddOptions<T>()…Bind(…) and GetSection(…).Get<T>() are all binding sites, in Program.cs or not.
# --untracked everywhere: a brand-new options file is exactly how a flag arrives, and
# plain `git grep` cannot see it until it is added.
CLASS_INDEX="$(git grep --untracked -E '^[[:space:]]*(public|internal)[^=(]*[[:space:]]class[[:space:]]+[A-Za-z0-9_]+' \
  -- "$SRC" | sed -E 's/^([^:]+):.*[[:space:]]class[[:space:]]+([A-Za-z0-9_]+).*$/\2 \1/' | sort -u)"

# "SECTION <name>" + "PROP <name> <type>" for what class $2 of file $1 declares directly;
# depth==1 keeps nested option types out of the parent's section (they recurse instead).
class_members() {
  awk -v cls="$2" '
    !inclass && $0 ~ ("class[[:space:]]+" cls "([^A-Za-z0-9_]|$)") { inclass = 1 }
    inclass {
      here = depth
      if (here == 1 && /const[[:space:]]+string[[:space:]]+SectionName/ && match($0, /"[^"]*"/))
        print "SECTION " substr($0, RSTART + 1, RLENGTH - 2)
      if (here == 1 && /^[[:space:]]*public[[:space:]]/ && /[{][[:space:]]*get;[[:space:]]*(set|init);/) {
        head = $0; sub(/[[:space:]]*[{].*$/, "", head)
        n = split(head, w, /[[:space:]]+/); print "PROP " w[n] " " w[n - 1]
      }
      depth += gsub(/[{]/, "") - gsub(/[}]/, "")
      if (here > 0 && depth == 0) exit
    }' "$1"
}

# EVERY file declaring class $1, empty when $1 is not a class here (string/int/TimeSpan/...);
# the in-shell name test keeps the scalar property types from spawning a lookup each.
# D2: first-hit-only dropped a same-named class in another namespace, or a partial-class half.
CLASS_NAMES=" $(printf '%s\n' "$CLASS_INDEX" | awk '{print $1}' | sort -u | tr '\n' ' ')"
class_files() {
  case "$CLASS_NAMES" in *" $1 "*) ;; *) return 0 ;; esac
  awk -v c="$1" '$1 == c { print $2 }' <<<"$CLASS_INDEX"
}

# emit_flags <file> <class> <key-prefix> <depth>: one token per bindable property,
# recursing into properties whose type is itself a class here (bound sub-sections).
emit_flags() {
  if [ "$4" -gt 3 ]; then return 0; fi
  class_members "$1" "$2" | while read -r kind name type; do
    [ "$kind" = "PROP" ] || continue
    echo "$3:$name"
    type="${type%\?}"; type="${type##*.}"
    case "$type" in
      *[!A-Za-z0-9_]*) ;;
      *) for nested in $(class_files "$type"); do
           emit_flags "$nested" "$type" "$3:$name" $(($4 + 1))
         done ;;
    esac
  done
}

# emit_type <type>: the type's own properties, from every file that declares it.
# The section const may live in one half of a partial class and the properties in the other.
emit_type() {
  local f sec fallback
  fallback="$(for f in $(class_files "$1"); do
      class_members "$f" "$1" | awk '$1 == "SECTION" { print $2; exit }'
    done | awk 'NR == 1')"
  for f in $(class_files "$1"); do
    sec="$(class_members "$f" "$1" | awk '$1 == "SECTION" { print $2; exit }')"
    emit_flags "$f" "$1" "${sec:-${fallback:-$1}}" 1
  done
}

# Statement-joined scan of every .cs file, not Program.cs only: 6 of the repo's options
# classes bind through AddOptions<T>().Bind(...), four of them from Extensions/*.cs.
CS_FILES="$(git ls-files -co --exclude-standard "$SRC" \
  | grep -E '\.cs$' \
  | while IFS= read -r file; do [ -f "$file" ] && printf '%s\n' "$file"; done \
  | sort -u)"
BOUND_TYPES="$(awk '
  FNR == 1 { stmt = "" }
  { stmt = stmt " " $0 }
  /;[[:space:]]*$/ {
    if (stmt ~ /GetSection\(|\.Bind\(/) {
      s = stmt
      while (match(s, /(Configure|AddOptions|Get)<[^<>]+>/)) {
        t = substr(s, RSTART, RLENGTH); s = substr(s, RSTART + RLENGTH)
        sub(/^[A-Za-z]+</, "", t); sub(/>$/, "", t); sub(/.*\./, "", t)
        # Get<string[]>/Get<int> read a key, not an options class; the read arm owns those.
        if (t ~ /^[A-Za-z_][A-Za-z0-9_]*$/) print t
      }
    }
    stmt = ""
  }' $CS_FILES | sort -u)"
if [ -z "$BOUND_TYPES" ]; then
  echo "FAIL: no options binding parsed from $CS_FILES —"
  echo "      the code arm would silently inventory nothing. Fix the parser."
  exit 1
fi
UNRESOLVED="$(for t in $BOUND_TYPES; do if [ -z "$(class_files "$t")" ]; then echo "$t"; fi; done)"
if [ -n "$UNRESOLVED" ]; then
  echo "FAIL: bound options class(es) not declared under $SRC, so the code arm"
  echo "      cannot inventory them: $(printf '%s ' $UNRESOLVED)"
  exit 1
fi
CODE_FLAGS="$(for t in $BOUND_TYPES; do emit_type "$t"; done \
  | sed 's/^FeatureFlags://' | sort -u)"
if [ -z "$CODE_FLAGS" ]; then
  echo "FAIL: the code arm produced 0 tokens from $(printf '%s\n' "$BOUND_TYPES" | grep -c .)"
  echo "      bound options class(es) — the property parser is broken, not the repo."
  exit 1
fi
# D2 read arm: keys read straight off IConfiguration with no options class in between —
# GetValue<T>("k") / GetSection("k") / config["k"] / a `…Key` const holding the literal.
READ_FLAGS="$({
    git grep --untracked -hoE '(GetValue<[^<>]*>|GetSection|GetConnectionString)\("[^"]+"' -- "$SRC"
    git grep --untracked -hoE '([A-Za-z0-9_.]*[Cc]onfig(uration)?|cfg)\[[[:space:]]*"[^"]+"' -- "$SRC"
    git grep --untracked -hoE 'const string [A-Za-z0-9_]*Key = "[^"]+:[^"]+"' -- "$SRC"
  } | sed -E 's/^[^"]*"//; s/"$//; s/^FeatureFlags://' | grep -vE '^[[:space:]]*$' | sort -u)"
MODE_TOKENS="$(git grep --untracked -howE '[A-Za-z]+Mode' -- "$SRC" | sort -u \
  | grep -vxF "$FRAMEWORK_MODE_TYPES" || true)"

# D3 deploy-env arm (G-05): workflows inject env-spelled keys the four arms above cannot see.
# `__`->`:` normalises to registry spelling; `#` lines drop so tombstone blocks stay mentions.
WORKFLOW_FILES="$(git ls-files -co --exclude-standard '.github/workflows' \
  | grep -E '\.ya?ml$' | sort -u || true)"
DEPLOY_ENV_FLAGS=""
if [ -n "$WORKFLOW_FILES" ]; then
  DEPLOY_ENV_FLAGS="$(grep -hvE '^[[:space:]]*#' $WORKFLOW_FILES \
    | grep -oE 'FeatureFlags__[A-Za-z0-9_]+' \
    | sed -E 's/^FeatureFlags__//; s/__/:/g' | grep -vE '^[[:space:]]*$' | sort -u || true)"
  # Parser-broken detector: a literal exists somewhere in the tree but the arm found none.
  if [ -z "$DEPLOY_ENV_FLAGS" ] && grep -qE 'FeatureFlags__' $WORKFLOW_FILES 2>/dev/null; then
    echo "FAIL: the deploy-env arm extracted 0 tokens although FeatureFlags__ literals"
    echo "      exist under .github/workflows — the extractor is broken, not the repo."
    exit 1
  fi
fi

INVENTORY="$(printf '%s\n%s\n%s\n%s\n%s\n' "$CONFIG_FLAGS" "$CODE_FLAGS" "$READ_FLAGS" \
  "$MODE_TOKENS" "$DEPLOY_ENV_FLAGS" | grep -vE '^[[:space:]]*$' | sort -u)"

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
echo "   arms: config=$(printf '%s\n' "$CONFIG_FLAGS" | grep -cE '.')" \
     "code=$(printf '%s\n' "$CODE_FLAGS" | grep -cE '.')" \
     "read=$(printf '%s\n' "$READ_FLAGS" | grep -cE '.')" \
     "mode=$(printf '%s\n' "$MODE_TOKENS" | grep -cE '.')" \
     "deploy-env=$(printf '%s\n' "$DEPLOY_ENV_FLAGS" | grep -cE '.')" \
     "(code arm: $(printf '%s\n' "$BOUND_TYPES" | grep -cE '.') bound options classes)"

# ---- Invariant (3): forbidden names are never active config keys ------------
echo "-- (3) forbidden names absent from active config keys, bound options, code reads and deploy env"
# D2/D3: GetValue<bool>("FeatureFlags:UseUpstream:Payments") and a deploy workflow's
# `--env-add FeatureFlags__UseUpstream__Payments=true` revive a retired name equally well.
active_forbidden="$(comm -12 <(printf '%s\n' "$FORBIDDEN") \
  <(printf '%s\n%s\n%s\n%s\n' "$ALL_CONFIG_KEYS" "$CODE_FLAGS" "$READ_FLAGS" \
      "$DEPLOY_ENV_FLAGS" | sort -u) || true)"
if [ -n "$active_forbidden" ]; then
  echo "FAIL: SUPERSEDED/retired name(s) active as a config key, a bound flag property or a deploy env var:"
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
