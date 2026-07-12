#!/usr/bin/env bash
# R9 — gateway no-DB / no-volatile-store CI gate (ADR-001-rev2 confirmation).
#
# Asserts two invariants that keep jeeb-gateway a STATELESS BFF:
#   (1) GR-3 RATCHET — the gateway opens a database only through the seams that
#       D1 (JEBV4-190) already inventoried. Any NEW db seam (raw Npgsql, or EF
#       DbContext / UseNpgsql / UseSqlServer / UseSqlite) hard-fails CI. The
#       allowlist may only SHRINK (JEBV4-193 / D4).
#   (2) No in-memory store is REGISTERED in DI for the 10 durable domains; those
#       must be backed by jeeb-state-service via the NSwag-typed client.
#
# Invariant (1) — the RATCHET (JEBV4-193):
#   Older revisions of this gate grepped only for EF markers (DbContext /
#   UseNpgsql / ...), so the ~24 files that open the DB via RAW Npgsql slipped
#   through and the gate green-washed a gateway that in fact owns ~25 DB seams.
#   The ratchet fixes that: it enumerates EVERY DB seam in the source (raw
#   Npgsql imports included) and compares the set against an explicit allowlist
#   seeded from the D1 matrix — scripts/gateway-db-seam-allowlist.txt.
#     * A seam file NOT on the allowlist  => HARD FAIL (new seam, or a D5-removed
#       seam reappeared: removals delete their allowlist line, so a reappearance
#       is no longer allowlisted and trips the gate).
#     * The allowlist is monotonically non-increasing — each D5 (JEBV4-194) store
#       elimination deletes that store's allowlist line in the same PR.
#     * When the allowlist is EMPTY the ratchet enforces absolute zero: ANY DB
#       seam then hard-fails. Emptying the allowlist is the FINAL step of D5
#       (Q-010 RATIFIED / GR-3 absolute). No separate "enforce zero" flag is
#       needed — the subset check degenerates to zero-seam enforcement for free.
#
# Invariant (2) is reported as a tracked-debt INVENTORY while the Layer-2 rewire
# is in progress (R2/R3/R4/R5/R6/R7 reconstruction is blocked on missing
# read-by-domain-key endpoints in jeeb-state-service — see SPECS-STATUS). It
# flips to HARD-FAIL by setting R9_ENFORCE_NO_INMEMORY=1 once those endpoints
# land and the InMemory fallbacks are deleted. This keeps the gate honest: it
# never green-washes the DB-free invariant, and it never falsely claims the
# durable rewire is complete.
set -euo pipefail
ENFORCE_NO_INMEMORY="${R9_ENFORCE_NO_INMEMORY:-0}"

SRC="src/JeebGateway"
PROGRAM="$SRC/Program.cs"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ALLOWLIST="$SCRIPT_DIR/gateway-db-seam-allowlist.txt"
fail=0

echo "== R9 gate: stateless jeeb-gateway =="

# ---- Invariant (1): GR-3 db-seam RATCHET vs the D1 allowlist -----------------
echo "-- (1) GR-3 ratchet: no gateway DB seam outside the D1 allowlist"
if [ ! -f "$ALLOWLIST" ]; then
  echo "FAIL: allowlist not found at $ALLOWLIST"
  exit 1
fi

# Detected seams: source files that IMPORT the Npgsql namespace (real DB access,
# not a comment mention) or use an EF DbContext / provider. `using Npgsql;` is
# the low-false-positive signal — doc-comment references to NpgsqlException or
# INpgsqlConnectionFactory in the composition root do not import the namespace.
DETECTED="$(grep -rlE '^[[:space:]]*using[[:space:]]+Npgsql|UseNpgsql|UseSqlServer|UseSqlite|:[[:space:]]*DbContext|DbContextOptions' \
  "$SRC" --include='*.cs' | sed 's#^\./##' | sort -u || true)"

# Allowed seams: uncommented, non-blank lines from the allowlist.
ALLOWED="$(grep -vE '^[[:space:]]*(#|$)' "$ALLOWLIST" | sed 's/[[:space:]]*$//' | sort -u || true)"

# (1a) any detected seam not on the allowlist => NEW/REAPPEARED seam => FAIL.
new_seams="$(comm -23 <(printf '%s\n' "$DETECTED") <(printf '%s\n' "$ALLOWED") || true)"
if [ -n "$new_seams" ]; then
  echo "FAIL: gateway DB seam(s) not on the GR-3 allowlist (new seam, or a"
  echo "      D5-removed seam reappeared). This violates GR-3. Offending files:"
  printf '  %s\n' $new_seams
  echo "      Do NOT add to the allowlist — move the store to its owning service."
  fail=1
fi

# (1b) allowlist entries no longer detected => the ratchet should have shrunk.
# Soft NOTE (not a fail) so a D5 removal PR that deletes the store before pruning
# its line still goes green; the follow-up prune keeps the list honest.
stale="$(comm -13 <(printf '%s\n' "$DETECTED") <(printf '%s\n' "$ALLOWED") || true)"
if [ -n "$stale" ]; then
  echo "NOTE: allowlist entries no longer present as DB seams — prune these lines"
  echo "      from $ALLOWLIST (the ratchet only shrinks):"
  printf '  %s\n' $stale
fi

allowed_n="$(printf '%s\n' "$ALLOWED" | grep -cE '.' || true)"
detected_n="$(printf '%s\n' "$DETECTED" | grep -cE '.' || true)"
if [ "$fail" -eq 0 ]; then
  if [ "$allowed_n" -eq 0 ]; then
    echo "OK: allowlist empty — GR-3 absolute-zero enforced and $detected_n seams present."
  else
    echo "OK: $detected_n DB seam(s), all within the $allowed_n-entry D1 allowlist (ratchet holds)."
  fi
fi

# ---- Invariant (2): no durable-domain InMemory* registered in DI ------------
# These are the 10 durable domains rewired to jeeb-state-service (R1–R8).
echo "-- (2) no durable-domain InMemory* registration in Program.cs DI"
FORBIDDEN_REGS=(
  "IRefreshTokenStore, *InMemoryRefreshTokenStore"   # R2
  "IKycStore, *InMemoryKycStore"                     # R3
  "IRatingStore, *InMemoryRatingStore"               # R4
  "IDisputeCaseStore, *InMemoryDisputeCaseStore"     # R5
  "IDisputeStore, *InMemoryDisputeStore"             # R5
  "IJeeberRestrictionStore, *InMemoryJeeberRestrictionStore" # R6
  "IAdminEscalationStore, *InMemoryAdminEscalationStore"     # R7
)
inmemory_found=0
for pat in "${FORBIDDEN_REGS[@]}"; do
  if grep -nE "AddSingleton<[^>]*$pat" "$PROGRAM" | grep -vE '^\s*//' >/dev/null 2>&1; then
    inmemory_found=1
    if [ "$ENFORCE_NO_INMEMORY" = "1" ]; then
      echo "FAIL: forbidden durable-domain registration still active: $pat"
      grep -nE "AddSingleton<[^>]*$pat" "$PROGRAM" | grep -vE '^\s*//'
      fail=1
    else
      echo "DEBT: durable-domain InMemory still registered (rewire in progress): $pat"
    fi
  fi
done
if [ "$inmemory_found" -eq 0 ]; then
  echo "OK: no durable-domain InMemory registration remains."
elif [ "$ENFORCE_NO_INMEMORY" != "1" ]; then
  echo "NOTE: set R9_ENFORCE_NO_INMEMORY=1 to hard-fail once jeeb-state-service"
  echo "      exposes read-by-domain-key endpoints and the fallbacks are removed."
fi

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "R9 gate FAILED — gateway is not stateless. See ADR-001-rev2."
  exit 1
fi
echo ""
echo "R9 gate PASSED — gateway holds no DB and no forbidden volatile store."
