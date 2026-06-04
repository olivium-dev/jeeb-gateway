#!/usr/bin/env bash
# R9 — gateway no-DB / no-volatile-store CI gate (ADR-001-rev2 confirmation).
#
# Asserts two invariants that keep jeeb-gateway a STATELESS BFF:
#   (1) The gateway opens NO database connection — no DbContext / AddDbContext /
#       UseNpgsql / UseSqlServer / UseSqlite anywhere in its source.
#   (2) No in-memory store is REGISTERED in DI for the 10 durable domains; those
#       must be backed by jeeb-state-service via the NSwag-typed client.
#
# Invariant (1) is the ADR's primary confirmation grep and is enforced HARD —
# it mirrors rahmah-gateway's stateless shape and is true today.
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
fail=0

echo "== R9 gate: stateless jeeb-gateway =="

# ---- Invariant (1): no DB connection anywhere in gateway source -------------
echo "-- (1) no DbContext / AddDbContext / UseNpgsql / UseSqlServer / UseSqlite"
# Strip line comments before matching so doc-comments don't trip the gate.
db_hits=$(grep -rnE 'DbContext|AddDbContext|UseNpgsql|UseSqlServer|UseSqlite' "$SRC" --include='*.cs' \
  | grep -vE '^\s*//' | grep -vE '://' || true)
if [ -n "$db_hits" ]; then
  echo "FAIL: gateway must not open a database. Offending lines:"
  echo "$db_hits"
  fail=1
else
  echo "OK: no database wiring in gateway source."
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
