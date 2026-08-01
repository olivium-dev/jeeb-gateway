#!/usr/bin/env bash
# =============================================================================
# GW1 TEST PACK — negative controls.
#
# A test that cannot fail is not evidence. For every assertion the pack makes,
# this harness breaks the thing the assertion is about, watches THAT assertion go
# red, and restores. A control "behaves" only if the named assertion reds AND the
# tree is byte-identical afterwards.
#
# ---------------------------------------------------------------------------
# IT NEVER TOUCHES THE LIVE WORKING TREE. This is not fastidiousness.
# OWNER-DECISIONS.md (2026-07-31 10:32Z) records a negative-control harness
# mutating a live file and manufacturing a FAIL on the batch that gate-blocks 28
# others — specific, plausible, correctly formatted, and forensically invisible,
# because `cp -p` preserved the mtime through the restore. So:
#   * every mutation happens in an isolated `git archive HEAD` export under the
#     session scratchpad;
#   * restores come from a pristine second copy, NOT from `cp -p`, so mtimes move
#     and an investigator asking "did this file change?" is told the truth;
#   * the harness takes the same lock run-pack.sh takes, so the two can never
#     interleave even if someone points this at a live tree by mistake.
# ---------------------------------------------------------------------------
#
# Usage:
#   tests/gw1-pack/neg-controls.sh              # all controls
#   tests/gw1-pack/neg-controls.sh N9 N14       # named controls only
#
# Exit: 0 = every control behaved; otherwise the number that did not.
# =============================================================================
set -uo pipefail

SRC_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRATCH="${GW1_NEGCTL_DIR:-${TMPDIR:-/tmp}/gw1-negctl}"
WORK="$SCRATCH/tree"
PRISTINE="$SCRATCH/pristine"

LOCK="$SRC_ROOT/tests/gw1-pack/.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "REFUSING TO RUN: $LOCK is held (run-pack.sh, or another neg-controls run)." >&2
  exit 2
fi
trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT

WANT=("$@")

echo "GW1 negative controls"
echo "source repo : $SRC_ROOT"
echo "HEAD        : $(cd "$SRC_ROOT" && git rev-parse HEAD)"
echo "isolated at : $WORK   (the live tree is never written)"

rm -rf "$SCRATCH"; mkdir -p "$WORK" "$PRISTINE"
(cd "$SRC_ROOT" && git archive HEAD) | tar -x -C "$WORK"
# The pack may not be committed yet when this is first run; overlay the live copy
# so the harness is usable before and identical after. Plain cp: mtimes MOVE.
cp -R "$SRC_ROOT/tests/gw1-pack" "$WORK/tests/" 2>/dev/null || true
cp -R "$SRC_ROOT/tests/JeebGateway.IntegrationTests/Gw1Pack" \
      "$WORK/tests/JeebGateway.IntegrationTests/" 2>/dev/null || true
# The overlay above runs while THIS script holds the outer lock, so the live
# .lock directory would be copied in and the inner run-pack.sh would refuse to
# start — reporting every assertion ABSENT and voiding every control. (Observed
# first-hand; recorded because a VOID that looks like a dead test is exactly the
# trap OWNER-DECISIONS.md documents for relocated harness copies.)
rm -rf "$WORK/tests/gw1-pack/.lock"
cp -R "$WORK/." "$PRISTINE/"

# The export has no .git, so pack assertions that shell out to git would break.
# Give it one, with origin/main pinned to the same base commit the live repo has.
BASE_SHA="$(cd "$SRC_ROOT" && git rev-parse origin/main)"
(
  cd "$WORK"
  git init -q .
  git add -A >/dev/null 2>&1
  git -c user.email=pack@local -c user.name=pack commit -qm "gw1 neg-control baseline" >/dev/null
  git update-ref refs/remotes/origin/main "$BASE_SHA" 2>/dev/null || true
)
# Ancestry/diff-based assertions need the real base OBJECT, not just the ref.
(cd "$WORK" && git remote add origin "$SRC_ROOT/.git" >/dev/null 2>&1
              git fetch -q origin main:refs/remotes/origin/main 2>/dev/null || true)

PACK="$WORK/tests/gw1-pack/run-pack.sh"
BEHAVED=0; MISBEHAVED=0; SKIPPED=0
MISBEHAVED_IDS=()

# run_pack <item> -> writes $LAST_OUT, returns the pack's exit code
LAST_OUT=""
run_pack() {
  local item="$1"
  LAST_OUT="$(cd "$WORK" && bash "$PACK" --item "$item" 2>&1)"
  return $?
}

# assertion_state <assertion-id> -> PASS | FAIL | ABSENT   (from $LAST_OUT)
assertion_state() {
  local id="$1"
  if printf '%s\n' "$LAST_OUT" | grep -qE "^FAIL $id( |$)"; then echo FAIL
  elif printf '%s\n' "$LAST_OUT" | grep -qE "^PASS $id( |$)"; then echo PASS
  else echo ABSENT; fi
}

# Fold the current working tree into the isolated HEAD commit.
#
# WHY THIS IS REQUIRED, and it cost four wrong control results to find: most of
# the pack's static assertions read git, not the filesystem —
#   `git grep`                      sees TRACKED files only, so a NEW file is invisible
#   `git show <ref>:<path>`         (critical-parse.py) reads a COMMIT, never the worktree
#   `git diff <base>..HEAD`         (added-lines-inert.py) compares two COMMITS
# A mutation left only in the working tree is therefore invisible to all three,
# and the control reports "the assertion did not red" when in truth the assertion
# was never shown the mutation. Committing it is also the faithful simulation:
# what a control asks is "if this had been WRITTEN, would the pack catch it?"
git_sync() {
  (cd "$WORK" && git add -A >/dev/null 2>&1 &&
   git -c user.email=pack@local -c user.name=pack commit -q --amend --no-edit --allow-empty >/dev/null 2>&1)
}

# Restore from the pristine copy. `--delete` undoes a mutation that ADDED a file;
# bin/ and obj/ are excluded so builds stay incremental.
#
# `--checksum --no-times` is load-bearing, and this also cost a wrong result.
# A plain `rsync -a` restore PRESERVES the pristine mtime, which is OLDER than the
# stale build output produced from the mutated source — so MSBuild considered the
# assembly up to date, skipped the rebuild, and the pack kept testing the MUTATED
# dll after the restore. Controls reported `during=FAIL after=FAIL` and every
# later control voided. Comparing by CONTENT and stamping restored files with the
# current time makes the restore visible to the build. (Note the family
# resemblance to the `cp -p` mtime trap in OWNER-DECISIONS.md 2026-07-31 10:32Z:
# the same preserved-mtime mechanism, one layer down.)
restore_all() {
  for d in src db tests/JeebGateway.IntegrationTests; do
    rsync -a --checksum --no-times --delete --exclude 'bin/' --exclude 'obj/' \
          "$PRISTINE/$d/" "$WORK/$d/"
  done
  git_sync
}

wanted() {
  [ ${#WANT[@]} -eq 0 ] && return 0
  local id="$1"; for w in "${WANT[@]}"; do [ "$w" = "$id" ] && return 0; done; return 1
}

# control <id> <item> <assertion-id> <description> <mutate-fn> [companion-id]
#
# `companion-id`, when given, is an assertion that must stay GREEN under the same
# mutation. That is what proves the pack's assertions are INDEPENDENT rather than
# one lump: N2 re-adds the deleted transport under another name, and the point is
# precisely that A1's symbol grep CANNOT see it while A5 can.
control() {
  local id="$1" item="$2" assertion="$3" desc="$4" mutate="$5" companion="${6:-}"
  if ! wanted "$id"; then SKIPPED=$((SKIPPED+1)); return; fi

  # baseline: the assertion must be PASSing before we break it, or the control is void
  run_pack "$item" >/dev/null 2>&1
  local before; before="$(assertion_state "$assertion")"
  if [ "$before" != "PASS" ]; then
    echo "VOID $id  [$assertion was $before at baseline — this control proves nothing]"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("$id"); return
  fi

  "$mutate"
  git_sync

  run_pack "$item" >/dev/null 2>&1
  local during; during="$(assertion_state "$assertion")"
  local evidence; evidence="$(printf '%s\n' "$LAST_OUT" | grep -E "^FAIL $assertion" | head -1)"
  # …and, when the red came from the xunit suite, the SPECIFIC test that failed.
  # "item W2.6 is red" is not actionable; "C4d_Pdf_Period_Is_The_Settlement_Day" is.
  local named; named="$(printf '%s\n' "$LAST_OUT" | grep -oE 'Failed +[A-Za-z0-9_.]+' | sed 's/Failed  *//' | sort -u | head -4)"
  local comp_state="n/a"
  [ -n "$companion" ] && comp_state="$(assertion_state "$companion")"

  restore_all
  run_pack "$item" >/dev/null 2>&1
  local after; after="$(assertion_state "$assertion")"

  local comp_ok=1
  [ -n "$companion" ] && [ "$comp_state" != "PASS" ] && comp_ok=0

  if [ "$during" = "FAIL" ] && [ "$after" = "PASS" ] && [ "$comp_ok" -eq 1 ]; then
    echo "OK   $id  $desc"
    echo "        -> $assertion went RED: ${evidence:-<no line captured>}"
    [ -n "$named" ] && printf '        -> failing test(s): %s\n' "$(printf '%s' "$named" | tr '\n' ' ')"
    [ -n "$companion" ] && echo "        -> $companion stayed PASS under the same mutation (the two are independent)"
    echo "        -> restored, $assertion PASS again"
    BEHAVED=$((BEHAVED+1))
  else
    echo "BAD  $id  $desc  [during=$during after=$after companion($companion)=$comp_state]"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("$id")
  fi
}

py() { python3 - "$@"; }   # readability helper

# =============================================================================
# ITEM W0.6
# =============================================================================
m_N1() {  # a bare mention of the deleted symbol, in a comment
  printf '\n// FcmPushTransport\n' >> "$WORK/src/JeebGateway/Push/InMemoryPushTransport.cs"
}
control N1 W0.6 A1 "a comment naming FcmPushTransport reappears under src/" m_N1

m_N2() {  # THE control that matters: the same transport, renamed
  cat > "$WORK/src/JeebGateway/Push/GooglePushTransport.cs" <<'CS'
namespace JeebGateway.Push;
public sealed class GooglePushTransport : IPushTransport
{
    public DevicePlatform Platform => DevicePlatform.Fcm;
    public Task SendAsync(DeviceToken device, PushNotificationRequest request, CancellationToken ct)
        => Task.CompletedTask;
}
CS
}
control N2 W0.6 A5_A9 "the deleted transport is re-added under a DIFFERENT name (A1's grep cannot see it)" m_N2 A1

m_N3() {  # …and it dials
  cat > "$WORK/src/JeebGateway/Push/GooglePushTransport.cs" <<'CS'
namespace JeebGateway.Push;
public sealed class GooglePushTransport : IPushTransport
{
    private readonly HttpClient _http;
    public GooglePushTransport(HttpClient http) => _http = http;
    public DevicePlatform Platform => DevicePlatform.Fcm;
    public Task SendAsync(DeviceToken device, PushNotificationRequest request, CancellationToken ct)
        => _http.PostAsync("https://fcm.googleapis.com/v1/x", null, ct);
}
CS
}
control N3 W0.6 A4 "a push type acquires an fcm.googleapis.com endpoint" m_N3

m_N4() {  # the config key returns
  py <<'PY' "$WORK/src/JeebGateway/appsettings.json"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace('"_comment_Fcm_REMOVED"', '"UseFcmTransport": false,\n    "_comment_Fcm_REMOVED"',1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N4 W0.6 A3 "a live UseFcmTransport KEY is put back in appsettings.json" m_N4

m_N5() {  # the switch property returns
  py <<'PY' "$WORK/src/JeebGateway/Push/PushOptions.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.rindex("}")
io.open(p,"w",encoding="utf-8").write(s[:i]+"    public bool UseFcmTransport { get; set; }\n"+s[i:])
PY
}
control N5 W0.6 A5_A9 "PushOptions regrows a transport switch property" m_N5

m_N6() {  # over-delete the platform discriminator's registration
  py <<'PY' "$WORK/src/JeebGateway/Program.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("builder.Services.AddSingleton<IPushTransport>(_ => new InMemoryPushTransport(DevicePlatform.Fcm));","",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N6 W0.6 A5_A9 "the InMemoryPushTransport(Fcm) registration is dropped" m_N6

# =============================================================================
# ITEM W1.8
# =============================================================================
CRIT_ROW='(typeof(JeebGateway.Financials.ISettlementLedgerClient),            new[] { typeof(JeebGateway.Financials.PostgresSettlementLedgerClient) }),'

m_N8() {  # remove the promotion outright
  py <<'PY' "$WORK/src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=re.sub(r"\n\s*\(typeof\(JeebGateway\.Financials\.ISettlementLedgerClient\).*?\}\),", "", s, count=1, flags=re.S)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N8 W1.8 B1 "the ISettlementLedgerClient row is removed from Critical" m_N8

m_N9() {  # THE control the sealed count cannot catch: swap, keeping the length at 33
  py <<'PY' "$WORK/src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
# drop a REAL pre-existing row and add a junk one: Critical.Length stays 33, so an
# in-suite `HaveCount(33)` stays GREEN while a real store of record went missing.
s=re.sub(r"\n\s*\(typeof\(JeebGateway\.Admin\.IAdminAuditLog\).*?\}\),", "", s, count=1, flags=re.S)
s=s.replace("(typeof(JeebGateway.Financials.ISettlementLedgerClient)",
            "(typeof(JeebGateway.Tokens.IRefreshTokenStore),                      new[] { typeof(JeebGateway.Tokens.StateServiceRefreshTokenStore) }),\n        (typeof(JeebGateway.Financials.ISettlementLedgerClient)",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N9 W1.8 B1 "a real Critical row is swapped out while the LENGTH stays 33 (a count cannot see this)" m_N9 B7

m_N10() {  # the owner's automatic-FAIL outcome
  py <<'PY' "$WORK/src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=re.sub(r"\n\s*\(typeof\(JeebGateway\.Financials\.ISettlementLedgerClient\).*?\}\),", "", s, count=1, flags=re.S)
s=s.replace("typeof(JeebGateway.Availability.IGeoIndex),",
            "typeof(JeebGateway.Financials.ISettlementLedgerClient),\n        typeof(JeebGateway.Availability.IGeoIndex),",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N10 W1.8 B2 "the ledger is parked on IntentionalInMemory (the owner's automatic-FAIL outcome)" m_N10

m_N11() {  # the durable selector is reverted
  py <<'PY' "$WORK/src/JeebGateway/Program.cs"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=re.sub(r"if \(!string\.IsNullOrWhiteSpace\(gatewayPostgresCs\)\)\s*\{\s*builder\.Services\.AddSingleton<ISettlementLedgerClient, PostgresSettlementLedgerClient>\(\);\s*\}\s*else\s*\{(.*?)\}",
         "builder.Services.AddSingleton<ISettlementLedgerClient, InMemorySettlementLedgerClient>();",
         s, count=1, flags=re.S)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N11 W1.8 B3_B6 "the ledger registration reverts to unconditional in-memory" m_N11

m_N12() {  # a column typo in the migration — the SILENT class
  py <<'PY' "$WORK/db/migrations/0044_init_settlement_ledger_entries.sql"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("    posted_at        TIMESTAMPTZ    NOT NULL,","    posted_at_v2     TIMESTAMPTZ    NOT NULL,",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N12 W1.8 B6py "migration 0044 renames a column the client writes (runtime 42703, swallowed)" m_N12

m_N13() {  # the money invariant: ON CONFLICT no longer targets the PK
  py <<'PY' "$WORK/src/JeebGateway/Financials/PostgresSettlementLedgerClient.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
# replace ALL occurrences: the FIRST one is in the class's XML doc, not the SQL,
# so a count=1 replace mutates prose and leaves the money path untouched.
s=s.replace("ON CONFLICT (idempotency_key) DO NOTHING","ON CONFLICT (delivery_id) DO NOTHING")
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N13 W1.8 B6py "ON CONFLICT stops targeting the PRIMARY KEY (a replay would mint a 2nd ledger id)" m_N13

m_N14() {  # make the in-memory memo survive a "restart" -> B5 must notice it stopped measuring the hazard
  py <<'PY' "$WORK/src/JeebGateway/Financials/InMemorySettlementLedgerClient.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("private readonly ConcurrentDictionary<string, LedgerEntryResponse> _entries",
            "private static readonly ConcurrentDictionary<string, LedgerEntryResponse> _entries",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N14 W1.8 B3_B6 "the in-memory ledger memo is made process-static, so B5 no longer observes the hazard" m_N14

m_N15() {  # migration numbering
  mv "$WORK/db/migrations/0044_init_settlement_ledger_entries.sql" \
     "$WORK/db/migrations/0045_init_settlement_ledger_entries.sql"
}
control N15 W1.8 B3_B6 "the migration is renumbered off 0044" m_N15

# =============================================================================
# ITEM W2.6
# =============================================================================
m_N16() {  # a new, uncovered action on a money controller
  py <<'PY' "$WORK/src/JeebGateway/Controllers/AdminSettlementsController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.index("    private static SettlementBatchResponse MapBatch")
s=s[:i]+'    [HttpDelete("batches/{id:guid}")]\n    public IActionResult Nuke(Guid id) => Ok();\n\n'+s[i:]
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N16 W2.6 C1_C5 "a new uncovered action is added to a money controller" m_N16

m_N17() {  # the capability marker is stripped
  py <<'PY' "$WORK/src/JeebGateway/Controllers/JeebWalletEarningsStatementsController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("    [RequireCapability(Capabilities.EarningsPdfOwn)]\n","",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N17 W2.6 C1_C5 "the PDF route loses its [RequireCapability] marker" m_N17

m_N18() {  # the money-report default window silently narrows
  py <<'PY' "$WORK/src/JeebGateway/Controllers/JeebWalletEarningsStatementsController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("TimeSpan.FromDays(90)","TimeSpan.FromDays(30)",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N18 W2.6 C1_C5 "the earnings default window narrows from 90 to 30 days" m_N18

m_N19() {  # an inverted window is forwarded instead of normalised
  py <<'PY' "$WORK/src/JeebGateway/Controllers/JeebWalletEarningsStatementsController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("        if (start > end)\n            (start, end) = (end, start);\n","",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N19 W2.6 C1_C5 "the inverted-window swap is removed (from>to reads as 'you earned nothing')" m_N19

m_N20() {  # the PDF bills the whole 90-day window onto one day's statement
  py <<'PY' "$WORK/src/JeebGateway/Controllers/JeebWalletEarningsStatementsController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("new EarningsStatementRequest(userId, dayStart, dayEnd, \"en\")",
            "new EarningsStatementRequest(userId, start, end, \"en\")",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N20 W2.6 C1_C5 "the PDF period widens from the settlement day to the whole lookup window" m_N20

# =============================================================================
# ITEM HYGIENE
# =============================================================================
m_N21() {  # a LIVE .50 reference
  py <<'PY' "$WORK/src/JeebGateway/Push/InMemoryPushTransport.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.rindex("}")
io.open(p,"w",encoding="utf-8").write(s[:i]+'    // placeholder\n'+s[i:]+'\npublic static class NegCtl { public const string BaseUrl = "http://192.168.2.50:10040"; }\n')
PY
}
control N21 HYGIENE H1 "a LIVE http://192.168.2.50:10040 BaseUrl is introduced" m_N21

m_N22() {  # a LIVE payments-gateway dial
  py <<'PY' "$WORK/src/JeebGateway/Financials/PostgresSettlementLedgerClient.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.rindex("}")
io.open(p,"w",encoding="utf-8").write(s[:i]+'    public const string Upg = "http://unified_payment_gateway:8080";\n'+s[i:])
PY
}
control N22 HYGIENE H3 "a LIVE unified_payment_gateway BaseUrl is introduced (cash-only policy)" m_N22

# =============================================================================
echo
echo "isolated tree left at: $WORK"
echo "live tree untouched  : $(cd "$SRC_ROOT" && git status --porcelain -- src/ db/ | wc -l | tr -d ' ') modified tracked path(s) under src/ db/"
echo "$BEHAVED control(s) behaved, $MISBEHAVED did not, $SKIPPED not selected"
[ ${#MISBEHAVED_IDS[@]} -gt 0 ] && echo "did not behave: ${MISBEHAVED_IDS[*]}"
exit $MISBEHAVED
