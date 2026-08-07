#!/usr/bin/env bash
# =============================================================================
# GW3 TEST PACK — runner.
#
# Reports PASS/FAIL **per member item**, because a pack that goes red as one lump
# cannot tell a verifier WHICH item broke, and GATE.md §8 requires exactly that
# (items are one commit each so a bad one can be reverted alone).
#
#   ITEM W3.5a     delete-not-wire the offer realtime notifier      A1..A9
#   ITEM W3.5c     delete the in-memory offer store + local accept  C0..C14
#                  + the false-comment fix
#   ITEM HYGIENE   batch-wide rules that belong to no single item   H1..H4
#
# Every assertion has a POSITIVE control (proof it can go green / proof the
# instrument is not blind) and a NEGATIVE control in tests/gw3-pack/neg-controls.sh
# (proof it can go red on the specific thing it is about).
#
# WHAT THIS PACK CANNOT PROVE, printed at the end of every run so the gap is never
# silent (GATE.md §3 — no amount of `static` + `build` + `suite` adds up to these):
#   * that MSI's live gateway still answers POST /v1/offers/{id}/accept   -> `service`
#   * that a real jeeber's 201 offer reaches a real customer's handset    -> `device`
# Both are GW3's Verifier-2 contract and neither is attempted here.
#
# Usage:
#   tests/gw3-pack/run-pack.sh                 # all items
#   tests/gw3-pack/run-pack.sh --item W3.5c    # one item
#   tests/gw3-pack/run-pack.sh --no-build      # reuse existing build output
#   GW3_BASE=origin/main tests/gw3-pack/run-pack.sh
#   GW3_FALSE_CLAIM_BASE=<sha> tests/gw3-pack/run-pack.sh   # A9/C7 POS control only
#
# Exit code = number of FAILING items (0 = all green).
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

BASE="${GW3_BASE:-origin/main}"

# The false-claim positive controls get their OWN, PINNED base and do not ride $BASE.
# $BASE means "the ref this branch is measured against", so it moves — and the day GW3
# merged, every false-claim POS control silently went red, because the claims it expects
# to find live at the base are gone from origin/main. A control that expires when the
# work ships is not a control. c3d5451 (GW5) is GW3's commit parent: the last tree in
# which every catalogued claim was live, and its content can never change.
# Override for a one-off with GW3_FALSE_CLAIM_BASE.
FALSE_CLAIM_BASE="${GW3_FALSE_CLAIM_BASE:-c3d5451}"
TESTPROJ="tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj"
LIB="tests/gw3-pack/lib"
GW1LIB="tests/gw1-pack/lib"     # added-lines-inert.py — already proven by GW1's pack
ONLY="all"
DO_BUILD=1
DO_DIFF=1

while [ $# -gt 0 ]; do
  case "$1" in
    --item) ONLY="$2"; shift 2 ;;
    --no-build) DO_BUILD=0; shift ;;
    # C14 builds and runs a SECOND copy of the suite at the base. neg-controls.sh
    # passes this for the 14 controls that are not about C14, so a 20-control run
    # does not rebuild the base export 20 times. C14 is then reported SKIPPED —
    # loudly, never silently — and its own control runs with the flag off.
    --no-differential) DO_DIFF=0; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# --item is matched with bare string equality below (`[ "$ONLY" = "W3.5a" ]`), which
# is exactly as forgiving of a typo/case slip (`w3.5a`, `W35a`, a stray space) as any
# other string compare: the mismatch does not error, it just never matches. Every `if`
# guarding an item block would then skip, FAILED_ITEMS would stay empty, and the
# summary at the bottom READS EMPTY AS GREEN — this pack would print "all items PASS"
# and exit 0 having executed zero assertions. Reproduced: `--item w3.5a` (case only)
# runs nothing and reports PASS today. Validate the value up front, the same way an
# unrecognized flag is already refused two lines up, so a bad --item is loud instead
# of vacuous.
case "$ONLY" in
  all|W3.5a|W3.5c|HYGIENE) ;;
  *)
    echo "REFUSING TO RUN: unknown --item '$ONLY' (want: all | W3.5a | W3.5c | HYGIENE)." >&2
    echo "A pack that cannot match its own --item silently runs zero assertions and would" >&2
    echo "otherwise report the vacuous 'all items PASS' below. That is not evidence." >&2
    exit 2
    ;;
esac

# --- exclusive lock -----------------------------------------------------------
# neg-controls.sh mutates source. If the two ever interleave, this runner reports a
# red that is real, correctly formatted, attributable to a real assertion — and
# entirely manufactured. That exact hazard already produced a false FAIL on the
# batch that gate-blocks 28 others (OWNER-DECISIONS.md, 2026-07-31 10:32Z).
# Portable mkdir lock; macOS has no flock.
LOCK="$ROOT/tests/gw3-pack/.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "REFUSING TO RUN: $LOCK is held (neg-controls.sh, or another pack run)." >&2
  echo "A pack result taken while source is mutated is not evidence." >&2
  exit 2
fi
trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT

FAILED_ITEMS=()
CUR_ITEM=""
CUR_FAILS=0
BUILD_RC=0
ITEMS_RUN=0   # belt-and-suspenders: the --item validation above should make this
              # unreachable, but the final verdict checks it directly rather than
              # trusting that an empty FAILED_ITEMS means "ran and passed" instead of
              # "never ran" — the exact ambiguity the vacuous "all items PASS" bug was.

item_begin() { CUR_ITEM="$1"; CUR_FAILS=0; ITEMS_RUN=$((ITEMS_RUN+1)); echo; echo "===== ITEM $1 — $2 ====="; }
item_end()   { if [ "$CUR_FAILS" -gt 0 ]; then echo "----- ITEM $CUR_ITEM: FAIL ($CUR_FAILS assertion(s)) -----"
                                               FAILED_ITEMS+=("$CUR_ITEM")
                                          else echo "----- ITEM $CUR_ITEM: PASS -----"; fi; }
ok()   { echo "PASS $1  $2"; }
bad()  { echo "FAIL $1  $2"; CUR_FAILS=$((CUR_FAILS+1)); }

assert_eq() { if [ "$3" = "$4" ]; then ok "$1" "$2  [$4]"; else bad "$1" "$2  [wanted $3, got $4]"; fi; }
assert_ge() { if [ "$4" -ge "$3" ] 2>/dev/null; then ok "$1" "$2  [$4 >= $3]"; else bad "$1" "$2  [wanted >= $3, got $4]"; fi; }

# The pack's OWN files necessarily NAME the symbols it forbids — a gate must be
# allowed to say what it bans (same reason scripts/no-50-allowlist.txt exists).
# Excluded hits are COUNTED AND PRINTED, never hidden.
EXCL=(':!tests/gw3-pack')

grep_files() { local pat="$1"; shift; git grep -I -l -F -- "$pat" "$@" "${EXCL[@]}" 2>/dev/null | wc -l | tr -d ' '; }
grep_lines() { local pat="$1"; shift; git grep -I -n -F -- "$pat" "$@" "${EXCL[@]}" 2>/dev/null | wc -l | tr -d ' '; }
grep_files_at() { local ref="$1" pat="$2"; shift 2; git grep -I -l -F -- "$pat" "$ref" "$@" 2>/dev/null | wc -l | tr -d ' '; }
grep_lines_at() { local ref="$1" pat="$2"; shift 2; git grep -I -n -F -- "$pat" "$ref" "$@" 2>/dev/null | wc -l | tr -d ' '; }
pack_mentions() { local pat="$1"; git grep -I -n -F -- "$pat" -- tests/gw3-pack 2>/dev/null | wc -l | tr -d ' '; }

run_filter() { # run_filter ID "desc" <xunit filter>
  local id="$1" desc="$2" filter="$3" out line
  if [ "$BUILD_RC" -ne 0 ]; then
    bad "$id" "$desc  [not run: dotnet build failed — see C0]"; return
  fi
  out="$(dotnet test "$TESTPROJ" --no-build --nologo --filter "$filter" 2>&1)"
  line="$(printf '%s\n' "$out" | grep -E '^(Passed!|Failed!|No test matches)' | head -1)"
  [ -z "$line" ] && line="$(printf '%s\n' "$out" | tail -3 | tr '\n' ' ')"
  if printf '%s\n' "$out" | grep -q '^Passed!'; then
    # A filter matching ZERO tests exits 0 on some runners. "no test matched" is
    # not "the tests passed" — refuse it.
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

echo "GW3 test pack"
echo "repo    : $ROOT"
echo "HEAD    : $(git rev-parse HEAD)"
echo "base    : $BASE = $(git rev-parse "$BASE" 2>/dev/null || echo '<unresolvable>')"
echo "ancestry: $(git merge-base --is-ancestor "$BASE" HEAD 2>/dev/null && echo "$BASE is an ancestor of HEAD (exit 0)" || echo 'NOT AN ANCESTOR')"
echo "tree    : $(git status --porcelain | wc -l | tr -d ' ') modified path(s)"

if [ "$DO_BUILD" -eq 1 ]; then
  echo
  echo '--- dotnet build src + FULL IntegrationTests (the gateway gates on build; never a `dotnet test` loop: Docker, hangs) ---'
  BUILD_OUT="$(dotnet build "$TESTPROJ" -v q --nologo 2>&1)"; BUILD_RC=$?
  printf '%s\n' "$BUILD_OUT" | grep -E ': error|^ok dotnet|[0-9]+ Error' | sort -u | head -20
  if [ "$BUILD_RC" -ne 0 ]; then
    printf '%s\n' "$BUILD_OUT" | tail -20
    echo "NOTE: the build failed. Static assertions below still run (they read git, not the"
    echo "      build output); every xunit leg is reported FAIL and attributed to C0."
  fi
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "W3.5a" ]; then
item_begin "W3.5a" "the in-process offer realtime notifier is DELETED, not re-wired"

  # A1/A2 — the delete, in a form that CAN print a zero. `git grep -c` cannot: it
  # prints per-file counts and on zero matches prints nothing and exits 1
  # (OWNER-DECISIONS.md, "Known UNSATISFIABLE verifier contracts").
  assert_eq A1 "no IOfferRealtimeNotifier symbol anywhere in the tree" 0 "$(grep_files IOfferRealtimeNotifier)"
  assert_eq A2 "no InMemoryOfferRealtimeNotifier symbol anywhere in the tree" 0 "$(grep_files InMemoryOfferRealtimeNotifier)"
  echo "       | (pack's own files mention them $(pack_mentions IOfferRealtimeNotifier)/$(pack_mentions InMemoryOfferRealtimeNotifier) time(s); excluded by design, printed not hidden)"

  # A1pos — the SAME grep form, same pathspec, finds the notifier that survives.
  # Without this, the two zeros above are indistinguishable from a blind grep.
  assert_ge A1pos "POS control: the same grep form finds IOfferPushNotifier" 2 "$(grep_files IOfferPushNotifier)"

  # A3 — the file itself. `git ls-tree` with a wrong path returns 0 lines AND exit 0
  # (the CB2 silent-zero trap), so the sibling check is not optional.
  assert_eq A3 "Availability/IOfferRealtimeNotifier.cs is not tracked at HEAD" 0 \
            "$(git ls-tree -r --name-only HEAD -- src/JeebGateway/Availability/IOfferRealtimeNotifier.cs | wc -l | tr -d ' ')"
  assert_eq A3pos "POS control: a sibling in the SAME directory IS tracked (the pathspec resolves)" 1 \
            "$(git ls-tree -r --name-only HEAD -- src/JeebGateway/Availability/UpstreamPendingOffersStore.cs | wc -l | tr -d ' ')"

  # A4 — the grep is measured against the base, where the symbols provably exist.
  assert_eq A4 "POS control at $BASE: IOfferRealtimeNotifier present (3 files / 7 lines)" \
            "3/7" "$(grep_files_at "$BASE" IOfferRealtimeNotifier -- src tests)/$(grep_lines_at "$BASE" IOfferRealtimeNotifier -- src tests)"
  assert_eq A4b "POS control at $BASE: InMemoryOfferRealtimeNotifier present (3 files / 5 lines)" \
            "3/5" "$(grep_files_at "$BASE" InMemoryOfferRealtimeNotifier -- src tests)/$(grep_lines_at "$BASE" InMemoryOfferRealtimeNotifier -- src tests)"

  # A5..A8 — the RUNTIME legs. A grep cannot see the seam re-added under another
  # name, and cannot tell "deleted the dead recorder" from "deleted the real push".
  run_filter A5_A8 "DI census / ctor census / customer still pushed / NotifyNewOfferAsync census" \
                   "FullyQualifiedName~Gw3Pack.W35a_"

  # A9 — the doc claim W3.5(a) exists to retire, with its own base POS control.
  # The label says CATALOGUED on purpose: this scanner matches a closed, hand-audited
  # list of patterns, so it can never support the universal "no false claim in src/".
  run_script A9 "no LIVE false realtime-event claim from the CATALOGUE in src/ (POS: live at $FALSE_CLAIM_BASE; self-test on every run)" \
             python3 "$LIB/false-claim-scan.py" --ref HEAD --base "$FALSE_CLAIM_BASE" --item W3.5a --controls

item_end
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "W3.5c" ]; then
item_begin "W3.5c" "the in-memory offer store + the local accept are DELETED; the false comment is fixed"

  # C0 — the batch's literal test-pack requirement, as its own assertion so a
  # compile break is attributed to this item instead of vanishing into an exit 99.
  assert_eq C0 "the FULL IntegrationTests project compiles after the fixture churn" 0 "$BUILD_RC"

  # C1 — the store symbol, with the batch document's own numbers recomputed at base.
  assert_eq C1 "no InMemoryPendingOffersStore symbol anywhere in the tree" 0 "$(grep_files InMemoryPendingOffersStore)"
  echo "       | (pack's own files mention it $(pack_mentions InMemoryPendingOffersStore) time(s); excluded by design)"
  assert_eq C1pos "POS control at $BASE: 15 files / 31 lines, of which tests/ = 10 files / 23 lines (GW3.md's '10 test files, 23 fixture references')" \
            "15/31/10/23" \
            "$(grep_files_at "$BASE" InMemoryPendingOffersStore -- src tests db docs)/$(grep_lines_at "$BASE" InMemoryPendingOffersStore -- src tests db docs)/$(grep_files_at "$BASE" InMemoryPendingOffersStore -- tests)/$(grep_lines_at "$BASE" InMemoryPendingOffersStore -- tests)"

  # C2 — the local accept helper, with the anti-OVER-deletion companion. Deleting
  # BOTH accept paths would satisfy a bare "AcceptInMemoryAsync == 0".
  assert_eq C2 "no AcceptInMemoryAsync symbol anywhere in the tree" 0 "$(grep_files AcceptInMemoryAsync)"
  assert_eq C2pos "POS control at $BASE: AcceptInMemoryAsync present (1 file / 3 lines)" \
            "1/3" "$(grep_files_at "$BASE" AcceptInMemoryAsync -- src)/$(grep_lines_at "$BASE" AcceptInMemoryAsync -- src)"
  assert_ge C2comp "anti-over-deletion: AcceptUpstreamAsync SURVIVES at HEAD" 1 "$(grep_lines AcceptUpstreamAsync -- src)"

  # C3 — production source ships no test seam. The surviving src/ mention is a
  # comment with no call parens, so the assertion is on the CALLABLE form.
  assert_eq C3 "no EnqueueForTest( call/declaration under src/" 0 \
            "$(git grep -I -n -E 'EnqueueForTest\s*\(' -- src 2>/dev/null | wc -l | tr -d ' ')"
  assert_ge C3pos "POS control: the EnqueueForTest( seam exists under tests/ (it MOVED, it was not deleted)" 1 \
            "$(git grep -I -n -E 'EnqueueForTest\s*\(' -- tests 2>/dev/null | wc -l | tr -d ' ')"

  # C4/C5/C6 — the seam instrument. Parses declarations and use sites at a REF, so
  # a mutation left only in the worktree cannot pass, and it reports WHAT it found.
  run_script C4 "Program.cs's IPendingOffersStore registration is UNCONDITIONAL, and src/ ships exactly one implementation" \
             python3 "$LIB/offer-seam-scan.py" --ref HEAD \
             --expect-unconditional --expect-no-concrete-uses \
             --expect-implementations UpstreamPendingOffersStore
  # C5 — the instrument's POS control: the same parser MUST report the old shape at
  # the base. An instrument that says UNCONDITIONAL everywhere proves nothing.
  run_script C5 "POS control at $BASE: the SAME parser reports CONDITIONAL + 2 implementations" \
             python3 "$LIB/offer-seam-scan.py" --ref "$BASE" \
             --expect-conditional \
             --expect-implementations InMemoryPendingOffersStore UpstreamPendingOffersStore

  # C6 — GW3's Verifier-2 contract, verbatim: "0 concrete injections outside the
  # registration". Reported separately from C4 so it can red alone.
  C6N="$(python3 "$LIB/offer-seam-scan.py" --ref HEAD --json | python3 -c 'import json,sys; print(len(json.load(sys.stdin)["concrete_uses_outside_declaration_and_registration"]))')"
  assert_eq C6 "V-2 contract: 0 concrete store injections outside the registration" 0 "$C6N"

  # C7 — the false-comment fix. This is GW3's headline, not a footnote: the batch
  # summary names it. Includes its own base POS control (the named comments ARE live
  # at $FALSE_CLAIM_BASE), so a green here cannot be a blind scan.
  #
  # THE LABEL SAYS "CATALOGUE" DELIBERATELY. It used to read "no LIVE false
  # in-memory-offer-store claim in src/" — a universal the implementation cannot
  # support, because the CATALOGUE is a closed list of hand-audited patterns. That
  # over-claim cost real coverage: GW3's independent verifier found three more stale
  # statements (two of them NotSupportedException messages in the very file the fix
  # commit edited) while this check was reporting green. They are FC5/FC6/FC7 now.
  # Found another? Add it — a green here is a statement about the catalogue.
  run_script C7 "no LIVE false in-memory-offer-store claim from the CATALOGUE in src/ (POS: live at $FALSE_CLAIM_BASE; self-test on every run)" \
             python3 "$LIB/false-claim-scan.py" --ref HEAD --base "$FALSE_CLAIM_BASE" --item W3.5c --controls

  # C8..C13 — the RUNTIME legs, including the decisive one: with the flag OFF the
  # accept still forwards upstream. The deleted branch never touched the client.
  run_filter C8_C13 "flag-off/flag-on store resolution, assembly census, accept forwards upstream, guard unchanged" \
                    "FullyQualifiedName~Gw3Pack.W35c_"

  # C14 — the 17 test files the fixture churn touched. C0 proves they COMPILE; this
  # proves the churn did not break one. NOT "they are all green": 4 of the 141 are
  # already red at $BASE on this machine (2 need Docker, 1 is order-dependent, 1 is a
  # standing failure), and a pack that reds on those blames GW3 for the machine. The
  # predicate is a DIFFERENTIAL — zero tests that pass at $BASE and fail at HEAD —
  # with every candidate re-run individually at BOTH refs before it is believed.
  if [ "$DO_DIFF" -eq 0 ]; then
    echo "SKIP C14  no NEW test failure vs $BASE  [--no-differential]"
  elif [ "$BUILD_RC" -ne 0 ]; then
    bad C14 "no NEW test failure vs $BASE  [not run: dotnet build failed — see C0]"
  else
    run_script C14 "no test that passes at $BASE fails on this branch (17 churned files)" \
               python3 "$LIB/suite-differential.py" --base "$BASE" --filter \
      "FullyQualifiedName~AvailabilityEndpointTests|FullyQualifiedName~BR1EnforcementTests|FullyQualifiedName~ClientVisibilityAndReceiptTests|FullyQualifiedName~CreateDoubleSubmitConcurrencyTests|FullyQualifiedName~FlatWithdrawAliasTests|FullyQualifiedName~OfferPushNotifierTests|FullyQualifiedName~OfferStateMachineSm2Tests|FullyQualifiedName~OffersEndpointTests|FullyQualifiedName~PostgresRequestExpiryAuthorityTests|FullyQualifiedName~RequestExpiryObserverTests|FullyQualifiedName~RequestExpirySweeperTests|FullyQualifiedName~RequestOffersEndpointTests|FullyQualifiedName~RequestOffersSeatTests|FullyQualifiedName~RequestExpiryMathParityTests|FullyQualifiedName~S09DeliveryReadScopingTests"
  fi

item_end
fi

# =============================================================================
if [ "$ONLY" = "all" ] || [ "$ONLY" = "HYGIENE" ]; then
item_begin "HYGIENE" "batch-wide rules that belong to no single member item"

  # H1 — anti-contamination. GW3's V-1 asks for "no GW5 commit in origin/main..HEAD",
  # which CANNOT FAIL as written: GW5 merged to origin/main before GW3 branched, so
  # the range is empty of GW5 commits no matter what the writer did. This runs the
  # real check (no foreign batch id in the range) AND proves the matcher is not blind
  # by finding GW5/GW1 in the two commits immediately behind the base.
  run_script H1 "no foreign-batch commit in $BASE..HEAD, with the matcher's own POS control" \
             python3 "$LIB/commit-scope.py" --base "$BASE" --head HEAD --batch GW3

  # H2 — owner exclusion 2. NOT "CI is green" (that row is out of scope entirely);
  # the assertion is that no workflow was TOUCHED.
  assert_eq H2 "no .github/ path changed by this branch" 0 \
            "$(git diff --name-only "$BASE"..HEAD -- .github/ | wc -l | tr -d ' ')"

  # H3 — private-host ban, scoped to lines this branch added.
  run_script H3 "no LIVE 192.168.2.50 reference added by this branch" \
             python3 "$GW1LIB/added-lines-inert.py" "$BASE" 192.168.2.50 \
             --allow tests/gw3-pack/ -- src/ db/ tests/
item_end
fi

# =============================================================================
cat <<'NOTPROVEN'

===== NOT PROVEN BY THIS PACK (printed every run, so the omission cannot be silent) =====
These are NOT failures and NOT passes. Per GATE.md §3 they are the wrong evidence
class for a static/build/suite pack, and no green above should be read as covering
them. They are exactly GW3's Verifier-2 contract:

  * POST /v1/offers/{id}/accept still answers on the LIVE MSI gateway   -> `service`
      C11 proves the gateway FORWARDS with the flag off, against an in-process
      double. It says nothing about offer-service existing or MSI being up.
  * a real jeeber's offer 201s and reaches the customer                 -> `device`
      A7 proves the push notifier is CALLED with the right recipient, with the push
      client replaced by an in-process recorder. Nothing here leaves the host.
  * that the accept path's NotSupportedException gap (GetAsync /
    WithdrawForJeeberAsync) does or does not fault in production        -> `service`
      C13 records that the gap is still open; it does not measure it.
NOTPROVEN

echo
if [ "$ITEMS_RUN" -eq 0 ]; then
  # An empty FAILED_ITEMS array is ambiguous between "ran and passed" and "never ran
  # a single assertion" — this branch exists so the second case can never print as
  # the first. See the --item validation above for how this used to be reachable.
  echo "GW3 PACK: 0 items ran for --item '$ONLY' — NOT a pass, NOT evidence." >&2
  exit 99
elif [ ${#FAILED_ITEMS[@]} -eq 0 ]; then
  echo "GW3 PACK: all items PASS ($ITEMS_RUN ran)"
else
  echo "GW3 PACK: ${#FAILED_ITEMS[@]} item(s) FAIL -> ${FAILED_ITEMS[*]}"
fi
exit ${#FAILED_ITEMS[@]}
