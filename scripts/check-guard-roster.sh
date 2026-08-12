#!/usr/bin/env bash
# G-08 / R1 — StoreDurabilityGuard roster manifest gate.
#
# Failure mode it closes: a store deletion PR removes the implementation but
# leaves its line on StoreDurabilityGuard.Critical, so the fail-closed boot
# guard 503s production on the next deploy. Invariants:
#   (0) SHAPE   — the generated roster is plausible (all four rosters present,
#       Critical not collapsed), so the gate cannot be neutered into a pass.
#   (1) DRIFT   — scripts/guard-roster.txt matches the four rosters in
#       StoreDurabilityGuard.cs (roster edit + manifest ship in the SAME PR).
#   (2) ORPHAN  — every type on the manifest is still declared under src/.
#   (3) G-18    — build.yml keeps the migration double-apply/seed gate.
#
# The C# roster stays the SOURCE OF TRUTH and the .txt is generated + drift
# checked: making DI read a text file at runtime would weaken the boot guard.
#
# Regenerate the manifest with: scripts/check-guard-roster.sh --write
set -euo pipefail

SRC="src/JeebGateway"
GUARD="$SRC/Infrastructure/StoreDurabilityGuard.cs"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="$SCRIPT_DIR/guard-roster.txt"
BUILD_WORKFLOW=".github/workflows/build.yml"
ROSTERS="Critical KnownInMemoryBacklog UpstreamContractIncomplete IntentionalInMemory"
# Critical held 33 entries at 66a7b9d; a parser regression collapses it to 0, while
# the planned W1–W5 store extractions remove far fewer than the 8 slack entries here.
CRITICAL_MIN=25
fail=0

# Emits "<Roster> <Iface>[ -> <Impl>[, <Impl>]]" per roster entry, in source
# order. Entries are read only inside the four roster initialisers.
generate_roster() {
  awk '
    function flush(buf,   s, arg, n, i, out) {
      n = 0
      s = buf
      while (match(s, /typeof\([^)]*\)/)) {
        arg = substr(s, RSTART + 7, RLENGTH - 8)
        gsub(/[[:space:]]/, "", arg)
        a[++n] = arg
        s = substr(s, RSTART + RLENGTH)
      }
      if (n == 0) return
      out = roster " " a[1]
      for (i = 2; i <= n; i++) out = out (i == 2 ? " -> " : ", ") a[i]
      print out
    }
    # Every brace style: opener at EOL, on the declaration line, or a C# 12
    # collection expression, so a harmless restyle cannot silently empty a roster.
    /^[[:space:]]*internal static readonly .*\[\][[:space:]]+[A-Za-z]+[[:space:]]*=[[:space:]]*[{[]?[[:space:]]*$/ {
      name = $0
      sub(/[[:space:]]*=[[:space:]]*[{[]?[[:space:]]*$/, "", name)
      sub(/.*[[:space:]]/, "", name)
      roster = name; buf = ""; next
    }
    roster != "" && /^[[:space:]]*[}\]];[[:space:]]*$/ {
      # A last entry may omit its trailing comma; flush it so it is not dropped.
      o = gsub(/\(/, "(", buf); c = gsub(/\)/, ")", buf)
      if (o == c) flush(buf)
      roster = ""; buf = ""; next
    }
    roster != "" {
      line = $0
      sub(/\/\/.*$/, "", line)
      if (line !~ /[^[:space:]]/) next
      buf = buf line
      opens = gsub(/\(/, "(", buf)
      closes = gsub(/\)/, ")", buf)
      trimmed = buf
      sub(/[[:space:]]*$/, "", trimmed)
      if (opens == closes && trimmed ~ /,$/) { flush(buf); buf = "" }
    }
  ' "$GUARD"
}

# Invariant (0). A parser regression (e.g. a restyled declaration) empties a roster
# silently; without this, --write would bake the collapse in and the gate would pass.
assert_roster_shape() {
  file="$1"
  shape_bad=0
  for name in $ROSTERS; do
    n="$(grep -cE "^${name}[[:space:]]" "$file" || true)"
    if [ "$n" -eq 0 ]; then
      echo "FAIL: generator emitted 0 entries for roster '$name' — it no longer parses"
      echo "      its declaration in $GUARD. Fix the generator; do NOT"
      echo "      regenerate the manifest, that neuters the G-08 gate. (invariant 0)"
      shape_bad=1
    fi
  done
  n="$(grep -cE '^Critical[[:space:]]' "$file" || true)"
  if [ "$n" -gt 0 ] && [ "$n" -lt "$CRITICAL_MIN" ]; then
    echo "FAIL: Critical roster collapsed to $n entries (floor $CRITICAL_MIN) — a real"
    echo "      shrink that large needs this floor lowered in the SAME PR. (invariant 0)"
    shape_bad=1
  fi
  [ "$shape_bad" -eq 0 ] || exit 1
}

if [ ! -f "$GUARD" ]; then
  echo "FAIL: $GUARD not found (run from the repo root)"
  exit 1
fi

if [ "${1:-}" = "--write" ]; then
  STAGED="$(mktemp)"
  trap 'rm -f "$STAGED"' EXIT
  generate_roster > "$STAGED"
  assert_roster_shape "$STAGED"
  cat "$STAGED" > "$MANIFEST"
  echo "wrote $MANIFEST ($(grep -cE '.' "$MANIFEST") entries)"
  exit 0
fi

echo "== G-08 gate: StoreDurabilityGuard roster manifest =="

# ---- Invariant (0): the generated roster is plausible ----------------------
echo "-- (0) SHAPE: generator still sees all four rosters"
GENERATED="$(mktemp)"
trap 'rm -f "$GENERATED"' EXIT
generate_roster > "$GENERATED"
assert_roster_shape "$GENERATED"
echo "OK: four rosters non-empty, Critical=$(grep -cE '^Critical[[:space:]]' "$GENERATED") (floor $CRITICAL_MIN)."

# ---- Invariant (1): manifest matches the C# rosters (R1 same-PR rule) -------
echo "-- (1) DRIFT: scripts/guard-roster.txt vs StoreDurabilityGuard.cs"
if [ ! -f "$MANIFEST" ]; then
  echo "FAIL: manifest not found at $MANIFEST"
  exit 1
fi

if ! diff -u "$MANIFEST" "$GENERATED" > /dev/null; then
  echo "FAIL: guard-roster.txt has drifted from StoreDurabilityGuard.cs."
  echo "      The roster edit and the manifest MUST ship in the SAME PR as the"
  echo "      store change (R1 / G-08) — a deleted store left on Critical"
  echo "      fail-closes production boot with a 503. Regenerate with:"
  echo "        scripts/check-guard-roster.sh --write"
  # diff exits 1 here; keep pipefail from aborting before invariants (2) and (3).
  diff -u "$MANIFEST" "$GENERATED" | sed 's/^/  /' || true
  fail=1
else
  echo "OK: manifest matches the four rosters ($(grep -cE '.' "$MANIFEST") entries)."
fi

# ---- Invariant (2): no manifest type without a declaration in src/ ---------
echo "-- (2) ORPHAN: every manifest type is still declared under $SRC"
orphans=0
while read -r roster iface rest; do
  [ -n "${iface:-}" ] || continue
  for fq in $(printf '%s %s\n' "$iface" "${rest#-> }" | tr ',' ' '); do
    short="${fq##*.}"
    if ! grep -rqE "(interface|class|record|struct)[[:space:]]+${short}([^A-Za-z0-9_]|\$)" \
        "$SRC" --include='*.cs'; then
      echo "FAIL: roster orphan: $fq ($roster) is on the manifest but no longer"
      echo "      declared under $SRC — delete its roster line in this PR."
      orphans=1
    fi
  done
done < <(grep -vE '^[[:space:]]*(#|$)' "$MANIFEST")
if [ "$orphans" -eq 0 ]; then
  echo "OK: all manifest types are still declared."
else
  fail=1
fi

# ---- Invariant (3): G-18 double-apply migration gate still present --------
echo "-- (3) G-18: build.yml keeps the migration double-apply gate"
if [ ! -f "$BUILD_WORKFLOW" ]; then
  echo "FAIL: $BUILD_WORKFLOW is missing — the migration double-apply gate is"
  echo "      gone. Keep the gate (or move it and update this check). G-18."
  fail=1
else
  applies="$(grep -cE '^[[:space:]]*run:[[:space:]]*\./db/apply\.sh[[:space:]]*$' "$BUILD_WORKFLOW" || true)"
  seeds="$(grep -cE '^[[:space:]]*run:[[:space:]]*\./db/seed\.sh[[:space:]]*$' "$BUILD_WORKFLOW" || true)"
  if [ "${applies:-0}" -lt 2 ] || [ "${seeds:-0}" -lt 2 ]; then
    echo "FAIL: $BUILD_WORKFLOW must run ./db/apply.sh and ./db/seed.sh TWICE"
    echo "      (second pass must no-op) — found apply=$applies seed=$seeds. G-18."
    fail=1
  else
    echo "OK: double-apply gate intact (apply=$applies seed=$seeds)."
  fi
fi

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "G-08 gate FAILED — roster manifest is not trustworthy. See guardrails G-08/G-18."
  exit 1
fi
echo ""
echo "G-08 gate PASSED — roster manifest in sync, no orphans, double-apply gate intact."
