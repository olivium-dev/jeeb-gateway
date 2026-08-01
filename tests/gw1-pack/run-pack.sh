#!/usr/bin/env bash
# =============================================================================
# GW1 TEST PACK — runner.
#
# Reports PASS/FAIL **per member item**, because a pack that goes red as one lump
# cannot tell a verifier WHICH item broke, and GATE.md §8 requires exactly that
# (items are one commit each so a bad one can be reverted alone).
#
#   ITEM W0.6      delete the FCM transport                        A1..A11
#   ITEM W1.8      durable settlement ledger + PROMOTE to Critical B1..B6b
#   ITEM W2.6      route tests for the 2 money controllers         C1..C5
#   ITEM HYGIENE   batch-wide rules that are not any one item      H1..H3
#
# Every assertion has a POSITIVE control in-line (proof the check can go green)
# and a NEGATIVE control in tests/gw1-pack/neg-controls.sh (proof it can go red).
#
# NOT PROVEN BY THIS PACK, and no green here should be read as proving it:
#   * that a settlement ledger entry survives a real restart  -> `service` class
#   * that a push ever reaches a handset                      -> `device`  class
# Both are printed at the end, every run, so the omission cannot be silent.
#
# Usage:
#   tests/gw1-pack/run-pack.sh                 # all items
#   tests/gw1-pack/run-pack.sh --item W1.8     # one item
#   tests/gw1-pack/run-pack.sh --no-build      # reuse the existing build output
#   GW1_BASE=origin/main tests/gw1-pack/run-pack.sh
#
# Exit code = number of FAILING items (0 = all green).
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

BASE="${GW1_BASE:-origin/main}"
TESTPROJ="tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj"
LIB="tests/gw1-pack/lib"
ONLY="all"
DO_BUILD=1

while [ $# -gt 0 ]; do
  case "$1" in
    --item) ONLY="$2"; shift 2 ;;
    --no-build) DO_BUILD=0; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# --- exclusive lock -----------------------------------------------------------
# neg-controls.sh mutates source. If the two ever interleave, this runner reports
# a red that is real, correctly formatted, attributable to a real assertion — and
# entirely manufactured. That exact hazard already produced a false FAIL on the
# batch that gate-blocks 28 others (OWNER-DECISIONS.md, 2026-07-31 10:32Z). A
# portable mkdir lock; no `flock` on macOS.
LOCK="$ROOT/tests/gw1-pack/.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "REFUSING TO RUN: $LOCK is held (neg-controls.sh, or another pack run)." >&2
  echo "A pack result taken while source is mutated is not evidence." >&2
  exit 2
fi
trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT

FAILED_ITEMS=()
CUR_ITEM=""
CUR_FAILS=0

item_begin() { CUR_ITEM="$1"; CUR_FAILS=0; echo; echo "===== ITEM $1 — $2 ====="; }
item_end()   { if [ "$CUR_FAILS" -gt 0 ]; then echo "----- ITEM $CUR_ITEM: FAIL ($CUR_FAILS assertion(s)) -----"
                                               FAILED_ITEMS+=("$CUR_ITEM")
                                          else echo "----- ITEM $CUR_ITEM: PASS -----"; fi; }
ok()   { echo "PASS $1  $2"; }
bad()  { echo "FAIL $1  $2"; CUR_FAILS=$((CUR_FAILS+1)); }

# assert_eq ID "desc" expected actual
assert_eq() { if [ "$3" = "$4" ]; then ok "$1" "$2  [$4]"; else bad "$1" "$2  [wanted $3, got $4]"; fi; }
# assert_ge ID "desc" floor actual
assert_ge() { if [ "$4" -ge "$3" ] 2>/dev/null; then ok "$1" "$2  [$4 >= $3]"; else bad "$1" "$2  [wanted >= $3, got $4]"; fi; }

# grep_count <pattern> [pathspec...] — literal, binary-safe, never aborts on exit 1
grep_count() { local pat="$1"; shift; git grep -I -n -F -- "$pat" "$@" 2>/dev/null | wc -l | tr -d ' '; }
grep_files() { local pat="$1"; shift; git grep -I -l -F -- "$pat" "$@" 2>/dev/null | wc -l | tr -d ' '; }

run_filter() { # run_filter ID "desc" <xunit filter>
  local id="$1" desc="$2" filter="$3" out
  out="$(dotnet test "$TESTPROJ" --no-build --nologo --filter "$filter" 2>&1)"
  local line
  line="$(printf '%s\n' "$out" | grep -E '^(Passed!|Failed!|No test matches)' | head -1)"
  [ -z "$line" ] && line="$(printf '%s\n' "$out" | tail -3 | tr '\n' ' ')"
  if printf '%s\n' "$out" | grep -q '^Passed!'; then
    # A filter that matches ZERO tests also exits 0 on some runners. Refuse that:
    # "no test matched" is not "the tests passed".
    if printf '%s\n' "$line" | grep -qE 'Passed: +0,'; then
      bad "$id" "$desc  [filter matched 0 tests — a vacuous green]"
    else
      ok "$id" "$desc  [$line]"
    fi
  else
    bad "$id" "$desc  [$line]"
    printf '%s\n' "$out" | grep -E '^\s+(Failed|Error Message)' | head -8 | sed 's/^/       | /'
  fi
}

run_script() { # run_script ID "desc" <cmd...>
  local id="$1" desc="$2"; shift 2
  local out; out="$("$@" 2>&1)"; local rc=$?
  if [ $rc -eq 0 ]; then ok "$id" "$desc"; printf '%s\n' "$out" | sed 's/^/       | /'
  else bad "$id" "$desc  [exit $rc]"; printf '%s\n' "$out" | sed 's/^/       | /'; fi
}

echo "GW1 test pack"
echo "repo   : $ROOT"
echo "HEAD   : $(git rev-parse HEAD)"
echo "base   : $BASE = $(git rev-parse "$BASE" 2>/dev/null || echo '<unresolvable>')"
echo "ancestry: $(git merge-base --is-ancestor "$BASE" HEAD 2>/dev/null && echo "$BASE is an ancestor of HEAD (exit 0)" || echo 'NOT AN ANCESTOR')"
echo "tree   : $(git status --porcelain | wc -l | tr -d ' ') modified path(s)"

if [ "$DO_BUILD" -eq 1 ]; then
  echo
  echo '--- dotnet build (the gateway gates on build; never a `dotnet test` loop: Docker, hangs) ---'
  BUILD_OUT="$(dotnet build "$TESTPROJ" -v q --nologo 2>&1)"; BUILD_RC=$?
  printf '%s\n' "$BUILD_OUT" | grep -E ': error|^ok dotnet|[0-9]+ Error' | sort -u | head -20
  if [ "${BUILD_RC:-0}" -ne 0 ]; then
    printf '%s\n' "$BUILD_OUT" | tail -20
    echo "FAIL BUILD  dotnet build did not succeed — every item below is unmeasurable"
    exit 99
  fi
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "W0.6" ]; then
item_begin "W0.6" "the in-gateway FCM transport is DELETED, not flag-disabled"

  # A1 — the delete, in a form that CAN print a zero. `git grep -c` cannot: it
  # prints per-file counts and on zero matches prints nothing and exits 1
  # (OWNER-DECISIONS.md, "Known UNSATISFIABLE verifier contracts").
  assert_eq A1 "no FcmPushTransport symbol anywhere under src/" 0 "$(grep_files FcmPushTransport -- src/)"
  A1POS="$(grep_files InMemoryPushTransport -- src/)"
  if [ "$A1POS" -gt 0 ]; then ok A1pos "POS control: the same grep form finds InMemoryPushTransport  [$A1POS file(s)]"
  else bad A1pos "POS control: grep reached src/ at all  [0 — the zero above proves nothing]"; fi

  # A2 — the anti-OVER-deletion leg. DevicePlatform.Fcm is the device-token
  # discriminator, not a transport; deleting it breaks routing while leaving
  # every "is the transport gone?" assertion green.
  assert_ge A2 "DevicePlatform.Fcm survives (platform discriminator)" 2 "$(grep_count 'DevicePlatform.Fcm' -- src/)"

  # A3 — KEY-level, not substring: the delete leaves a _comment_Fcm_REMOVED
  # tombstone that names all three keys, so a grep reds on correct work.
  run_script A3 "no live Fcm/transport-switch KEY in any appsettings*.json" python3 "$LIB/appsettings-keys.py"

  # A4 — nothing dials a push provider. The POS control matters: an unrelated
  # Firebase identitytoolkit URL legitimately survives in the chat token minter,
  # so a blanket 'googleapis.com' == 0 would be a false red.
  assert_eq A4 "no fcm.googleapis.com endpoint under src/" 0 "$(grep_count 'fcm.googleapis.com' -- src/)"
  A4POS="$(grep_count 'identitytoolkit.googleapis.com' -- src/)"
  assert_ge A4pos "POS control: the same grep finds the surviving (unrelated) Google endpoint" 1 "$A4POS"

  run_filter A5_A9 "assembly/DI/options/config legs (Gw1Pack.W06_)" "FullyQualifiedName~Gw1Pack.W06_"
  run_filter A10   "the writer's own push registration tests still green" \
                   "FullyQualifiedName~Push.PushTransportRegistrationTests"

item_end
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "W1.8" ]; then
item_begin "W1.8" "durable settlement ledger + PROMOTE to StoreDurabilityGuard.Critical"

  # B1 — measured, not asserted. An in-suite HaveCount(33) reports the number the
  # writer chose to write down; this parses the C# at BOTH refs and computes the
  # delta, which a count cannot see: 32 -> 33 is also reachable by removing a real
  # row and adding two junk ones.
  run_script B1 "Critical delta $BASE -> HEAD is exactly +ISettlementLedgerClient" \
             python3 "$LIB/critical-parse.py" --delta "$BASE" HEAD --expect-added ISettlementLedgerClient

  # B2 — the owner's automatic-FAIL case, stated as its own assertion.
  INTENTIONAL="$(python3 "$LIB/critical-parse.py" --ref HEAD --json | python3 -c 'import json,sys; print(",".join(json.load(sys.stdin)["intentional_in_memory_short"]))')"
  assert_eq B2 "IntentionalInMemory is unchanged (the ledger is NOT parked there)" \
            "IGeoIndex,ILocationStore" "$INTENTIONAL"

  # B6py — second, independent instrument on the same claim as the in-suite B6.
  run_script B6py "migration 0044 <-> PostgresSettlementLedgerClient agree (python)" \
             python3 "$LIB/schema-contract.py"

  run_filter B3_B6 "boot/selector/hazard/schema legs (Gw1Pack.W18_)" "FullyQualifiedName~Gw1Pack.W18_"
  run_filter B7    "the writer's own ledger + guard tests still green" \
                   "FullyQualifiedName~Financials.PostgresSettlementLedgerClientTests"
  run_filter B8    "the pre-existing fail-closed guard suite is not regressed" \
                   "FullyQualifiedName~Infrastructure.StoreDurabilityFailClosedTests"

item_end
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "W2.6" ]; then
item_begin "W2.6" "route tests for the two money controllers"

  run_filter C1_C5 "census/routing-table/capability/window/discriminator (Gw1Pack.W26_)" \
                   "FullyQualifiedName~Gw1Pack.W26_"
  run_filter C6    "the writer's AdminSettlementsController route tests" \
                   "FullyQualifiedName~AdminSettlementsControllerRouteTests"
  run_filter C7    "the writer's earnings-statements route tests" \
                   "FullyQualifiedName~JeebWalletEarningsStatementsControllerRouteTests"

item_end
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "HYGIENE" ]; then
item_begin "HYGIENE" "batch-wide rules that belong to no single member item"

  # H1 — the .50 ban, scoped to this branch's ADDED lines. A whole-tree count reds
  # on the 62 pre-existing inert mentions (AGENTS.md) that GW1 never touched.
  # --allow tests/gw1-pack/: the pack's OWN negative-control harness injects a
  # live-looking http://192.168.2.50:10040 (control N21) to prove H1 can go red.
  # A gate must be allowed to name what it forbids — the same reason
  # scripts/no-50-allowlist.txt exists. Allowed hits are PRINTED, not hidden.
  run_script H1 "no LIVE 192.168.2.50 reference added by this branch" \
             python3 "$LIB/added-lines-inert.py" "$BASE" 192.168.2.50 \
             --allow tests/gw1-pack/ -- src/ db/ tests/

  # H2 — no GitHub Actions work (owner exclusion 2). NOT "CI is green" — that row
  # is out of scope entirely; the assertion is that no workflow was TOUCHED.
  assert_eq H2 "no .github/ path changed by this branch" 0 \
            "$(git diff --name-only "$BASE"..HEAD -- .github/ | wc -l | tr -d ' ')"

  # H3 — cash-only. Same shape as H1, and it MUST be, because GW1 legitimately adds
  # one line naming the token: migration 0044's "NOT a payments-gateway surface.
  # There is no unified_payment_gateway dependency here and none may be added." A
  # bare added-line count reds on a comment that states the policy it obeys.
  run_script H3 "no LIVE unified_payment_gateway reference added by this branch" \
             python3 "$LIB/added-lines-inert.py" "$BASE" unified_payment_gateway \
             --allow tests/gw1-pack/ -- src/ db/ tests/

item_end
fi

# =============================================================================
cat <<'NOTPROVEN'

===== NOT PROVEN BY THIS PACK (printed every run, so the gap cannot be silent) =====
These are NOT failures and NOT passes. Per GATE.md §3 they are the wrong evidence
class for a host suite, and per §4 an honest NOT-PROVEN never counts as a pass.

  service  W1.8's actual claim — a ledger entry SURVIVES a restart.
           Nothing here executes the SQL: Testcontainers needs Docker, which is
           banned in this environment, and no host test can restart a process and
           re-read a database. The pack proves the SELECTOR, the GUARD, the
           SCHEMA/CODE agreement and the HAZARD; it cannot prove the round trip.
           V-2 leg:  settle one COD delivery -> record the ledger entry id
                  -> docs/agents/scripts/msi.sh --sudo systemctl restart jeeb-gateway
                  -> read the SAME id back.
           PRECONDITION, and it is a hard one: ISettlementLedgerClient is now
           Critical, so a prod-like boot REFUSES to start unless
           GatewayPostgres:ConnectionString is set, and migration 0044 must be
           applied or every post throws 42P01 SILENTLY (SettlementService swallows
           it; ledger_entry_id stays NULL and the 60 s reconciler retries forever).

  device   W0.6 says nothing about whether push works. InMemoryPushTransport
           enqueues to a ConcurrentQueue and the send is then counted Delivered.
           The claim under test is only that the gateway no longer CONTAINS a path
           that speaks to a push provider itself.
NOTPROVEN

echo
if [ ${#FAILED_ITEMS[@]} -eq 0 ]; then
  echo "RESULT: all items PASS"
  exit 0
fi
echo "RESULT: ${#FAILED_ITEMS[@]} item(s) FAIL -> ${FAILED_ITEMS[*]}"
exit ${#FAILED_ITEMS[@]}
