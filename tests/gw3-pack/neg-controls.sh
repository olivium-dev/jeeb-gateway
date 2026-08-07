#!/usr/bin/env bash
# =============================================================================
# GW3 TEST PACK — negative controls.
#
# A test that cannot fail is not evidence. For every assertion the pack makes,
# this harness breaks the thing the assertion is about, watches THAT assertion go
# red, and restores. A control "behaves" only if the named assertion reds AND is
# green again after the restore.
#
# ---------------------------------------------------------------------------
# IT NEVER TOUCHES THE LIVE WORKING TREE. OWNER-DECISIONS.md (2026-07-31 10:32Z)
# records a negative-control harness mutating a live file and manufacturing a
# FAIL on the batch that gate-blocks 28 others — specific, plausible, correctly
# formatted, and forensically invisible, because `cp -p` preserved the mtime
# through the restore. So:
#   * every mutation happens in an isolated `git archive HEAD` export under the
#     session scratchpad;
#   * restores come from a pristine second copy with `rsync --checksum --no-times`,
#     NOT `cp -p`, so mtimes MOVE and an investigator asking "did this file
#     change?" is told the truth — and MSBuild sees the restore;
#   * the harness takes the same lock run-pack.sh takes, so the two can never
#     interleave even if someone points this at a live tree by mistake.
# ---------------------------------------------------------------------------
#
# TWO CONTROL SHAPES:
#
#   control        baseline PASS -> mutate -> assert FAIL -> restore -> assert PASS.
#   repair_control for an assertion that is ALREADY RED on this branch. You cannot
#                  "break" what is already broken, so it runs the other way round:
#                  REPAIR (in the export only) -> assert PASS  [POSITIVE control:
#                  the assertion is SATISFIABLE, so the live red is a work defect
#                  and not an unsatisfiable contract] -> re-break -> assert FAIL
#                  [NEGATIVE control] -> restore.
#                  C7 is red at HEAD and gets this treatment.
#
# C14 is also red at HEAD but is NOT repairable in the export — one of its three
# regressions is a real behaviour change, not a fixture oversight (see C14REPAIR,
# which measures exactly that: 3 -> 1). Its controls are therefore on the
# INSTRUMENT: C14POS proves the differential can return PASS, C14NEG proves it
# names an injected regression, C14REPAIR bounds how much of the live red is
# fixture churn.
#
# Usage:
#   tests/gw3-pack/neg-controls.sh              # all controls
#   tests/gw3-pack/neg-controls.sh N4 N11       # named controls only
#
# Exit: 0 = every control behaved; otherwise the number that did not.
# =============================================================================
set -uo pipefail

SRC_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRATCH="${GW3_NEGCTL_DIR:-${TMPDIR:-/tmp}/gw3-negctl}"
WORK="$SCRATCH/tree"
PRISTINE="$SCRATCH/pristine"

LOCK="$SRC_ROOT/tests/gw3-pack/.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "REFUSING TO RUN: $LOCK is held (run-pack.sh, or another neg-controls run)." >&2
  exit 2
fi
trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT

WANT=("$@")

echo "GW3 negative controls"
echo "source repo : $SRC_ROOT"
echo "HEAD        : $(cd "$SRC_ROOT" && git rev-parse HEAD)"
echo "isolated at : $WORK   (the live tree is never written)"

rm -rf "$SCRATCH"; mkdir -p "$WORK" "$PRISTINE"
(cd "$SRC_ROOT" && git archive HEAD) | tar -x -C "$WORK"
# The pack may not be committed when this is first run; overlay the live copy so
# the harness is usable before and identical after. Plain cp: mtimes MOVE.
cp -R "$SRC_ROOT/tests/gw3-pack" "$WORK/tests/" 2>/dev/null || true
cp -R "$SRC_ROOT/tests/JeebGateway.IntegrationTests/Gw3Pack" \
      "$WORK/tests/JeebGateway.IntegrationTests/" 2>/dev/null || true
# This script holds the OUTER lock, so the live .lock dir would be copied in and
# the inner run-pack.sh would refuse to start — reporting every assertion ABSENT
# and voiding every control. (Observed first-hand on the GW1 pack; recorded there
# and inherited here.)
rm -rf "$WORK/tests/gw3-pack/.lock"
cp -R "$WORK/." "$PRISTINE/"

# The export has no .git, and most of this pack's assertions read git (grep at a
# ref, ls-tree, log, diff, `git show <ref>:<path>`). Give it one, with a REAL
# origin/main object fetched from the source repo — a ref pointing at nothing
# makes every POS control void.
BASE_SHA="$(cd "$SRC_ROOT" && git rev-parse origin/main)"
(
  cd "$WORK"
  git init -q .
  git add -A >/dev/null 2>&1
  # The subject MUST name GW3: H1 reads commit SUBJECTS, so an untagged baseline
  # commit reds H1 before any control runs and voids every HYGIENE control.
  git -c user.email=pack@local -c user.name=pack \
      commit -qm "GW3 W3.5(a)+W3.5(c): neg-control baseline (squash of the branch)" >/dev/null
  git remote add origin "$SRC_ROOT" >/dev/null 2>&1
  # NOT `origin main:refs/remotes/origin/main`. The source repo has a LOCAL `main`
  # branch that is 100+ commits behind origin/main; fetching it silently pins the
  # export's base to the wrong commit and every base-side POS control (A4, C1pos,
  # C5, C7's base leg, H1's POS range, H3/H4) then measures the wrong tree while
  # reporting green. Observed first-hand: the first run of this harness resolved
  # origin/main to 8d8a660 instead of the real base.
  git fetch -q origin '+refs/remotes/origin/main:refs/remotes/origin/main' 2>/dev/null || true
)
GOT_BASE="$(cd "$WORK" && git rev-parse origin/main 2>/dev/null || echo UNRESOLVED)"
echo "export base : origin/main = $GOT_BASE"
if [ "$GOT_BASE" != "$BASE_SHA" ]; then
  echo "REFUSING TO RUN: the export's origin/main is $GOT_BASE but the source repo's is" >&2
  echo "$BASE_SHA. Every base-side positive control would measure the wrong tree." >&2
  exit 2
fi

PACK="$WORK/tests/gw3-pack/run-pack.sh"
BEHAVED=0; MISBEHAVED=0; SKIPPED=0
MISBEHAVED_IDS=()

# C14 rebuilds and re-runs the whole suite at the base. Every control that is not
# ABOUT C14 passes --no-differential so a 20-control run does not do that 20 times;
# C14's own control clears this array. The skip is PRINTED by run-pack.sh.
PACK_EXTRA=(--no-differential)

LAST_OUT=""
run_pack() {  # run_pack <item>
  local item="$1"
  # ${A[@]+"${A[@]}"} — bash 3.2 (what macOS ships) treats "${A[@]}" on an EMPTY
  # array as an unbound variable under `set -u`. That produced NO pack output at all,
  # and C14's control reported ABSENT instead of a verdict. Not hypothetical: it is
  # exactly how the first full run lost C14CTL.
  LAST_OUT="$(cd "$WORK" && bash "$PACK" --item "$item" ${PACK_EXTRA[@]+"${PACK_EXTRA[@]}"} 2>&1)"
  return $?
}

assertion_state() {
  local id="$1"
  if printf '%s\n' "$LAST_OUT" | grep -qE "^FAIL $id( |$)"; then echo FAIL
  elif printf '%s\n' "$LAST_OUT" | grep -qE "^PASS $id( |$)"; then echo PASS
  else echo ABSENT; fi
}

# Fold the working tree into the isolated HEAD commit.
#
# REQUIRED, and it is the single easiest way to get four wrong control results:
# most of this pack reads git, not the filesystem —
#   `git grep`                sees TRACKED files only, so a NEW file is invisible
#   `git show <ref>:<path>`   (offer-seam-scan / false-claim-scan) reads a COMMIT
#   `git diff <base>..HEAD`   (added-lines-inert) compares two COMMITS
# A mutation left only in the worktree is invisible to all three and the control
# reports "the assertion did not red" when the assertion was never shown it.
git_sync() {
  (cd "$WORK" && git add -A >/dev/null 2>&1 &&
   git -c user.email=pack@local -c user.name=pack commit -q --amend --no-edit --allow-empty >/dev/null 2>&1)
}

# `--checksum --no-times` is load-bearing. A plain `rsync -a` restore PRESERVES the
# pristine mtime, which is OLDER than the build output produced from the mutated
# source, so MSBuild considers the assembly up to date and the pack keeps testing
# the MUTATED dll after the restore. Same preserved-mtime mechanism as the `cp -p`
# trap in OWNER-DECISIONS.md 2026-07-31 10:32Z, one layer down.
restore_all() {
  for d in src tests/JeebGateway.IntegrationTests tests/gw3-pack; do
    rsync -a --checksum --no-times --delete --exclude 'bin/' --exclude 'obj/' --exclude '.lock' \
          "$PRISTINE/$d/" "$WORK/$d/"
  done
  git_sync
}

wanted() {
  [ ${#WANT[@]} -eq 0 ] && return 0
  local id="$1"; for w in "${WANT[@]}"; do [ "$w" = "$id" ] && return 0; done; return 1
}

# control <id> <item> <assertion> <desc> <mutate-fn> [companion] [pack-args...]
#
# `companion`, when given, is an assertion that must stay GREEN under the same
# mutation. That is what proves the pack's assertions are INDEPENDENT rather than
# one lump: N4 re-adds the deleted seam under a different name, and the point is
# precisely that A1's symbol grep CANNOT see it while the runtime census can.
control() {
  local id="$1" item="$2" assertion="$3" desc="$4" mutate="$5" companion="${6:-}" unmutate="${7:-}"
  if ! wanted "$id"; then SKIPPED=$((SKIPPED+1)); return; fi

  run_pack "$item" >/dev/null 2>&1
  local before; before="$(assertion_state "$assertion")"
  if [ "$before" != "PASS" ]; then
    echo "VOID $id  [$assertion was $before at baseline — this control proves nothing]"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("$id"); return
  fi

  "$mutate"; git_sync
  run_pack "$item" >/dev/null 2>&1
  local during; during="$(assertion_state "$assertion")"
  local evidence; evidence="$(printf '%s\n' "$LAST_OUT" | grep -E "^FAIL $assertion" | head -1)"
  local named; named="$(printf '%s\n' "$LAST_OUT" | grep -oE 'Failed +[A-Za-z0-9_.]+' | sed 's/Failed  *//' | sort -u | head -4)"
  local comp_state="n/a"
  [ -n "$companion" ] && comp_state="$(assertion_state "$companion")"

  # `unmutate` exists because restore_all only rsyncs src/ and tests/. A mutation
  # that adds a COMMIT or writes outside those trees (N17, N18) survives it, and the
  # control would then report BAD for a reason that is the harness's fault, not the
  # pack's.
  [ -n "$unmutate" ] && "$unmutate"
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

# repair_control <id> <item> <assertion> <desc> <repair-fn> <rebreak-fn> [pack-args...]
# For an assertion that is ALREADY RED at HEAD. See the header.
repair_control() {
  local id="$1" item="$2" assertion="$3" desc="$4" repair="$5" rebreak="$6"
  if ! wanted "$id"; then SKIPPED=$((SKIPPED+1)); return; fi

  run_pack "$item" >/dev/null 2>&1
  local before; before="$(assertion_state "$assertion")"
  if [ "$before" != "FAIL" ]; then
    echo "VOID $id  [$assertion was $before at baseline; repair_control is for an already-RED assertion]"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("$id"); return
  fi

  "$repair"; git_sync
  run_pack "$item" >/dev/null 2>&1
  local repaired; repaired="$(assertion_state "$assertion")"
  local pos_evidence; pos_evidence="$(printf '%s\n' "$LAST_OUT" | grep -E "^PASS $assertion" | head -1)"

  "$rebreak"; git_sync
  run_pack "$item" >/dev/null 2>&1
  local rebroken; rebroken="$(assertion_state "$assertion")"
  local neg_evidence; neg_evidence="$(printf '%s\n' "$LAST_OUT" | grep -E "^FAIL $assertion" | head -1)"

  restore_all

  if [ "$repaired" = "PASS" ] && [ "$rebroken" = "FAIL" ]; then
    echo "OK   $id  $desc"
    echo "        -> POS: after the minimal repair, $assertion PASSES — the assertion is"
    echo "               SATISFIABLE, so the red on the live branch is a WORK defect, not"
    echo "               an unsatisfiable contract."
    echo "               ${pos_evidence:-<no line captured>}"
    echo "        -> NEG: re-broken, $assertion RED again: ${neg_evidence:-<no line captured>}"
    BEHAVED=$((BEHAVED+1))
  else
    echo "BAD  $id  $desc  [repaired=$repaired rebroken=$rebroken]"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("$id")
  fi
}

py() { python3 - "$@"; }

# =============================================================================
# ITEM W3.5a — delete-not-wire the offer realtime notifier
# =============================================================================
m_N1() { printf '\n// IOfferRealtimeNotifier\n' >> "$WORK/src/JeebGateway/Availability/UpstreamPendingOffersStore.cs"; }
control N1 W3.5a A1 "a comment naming IOfferRealtimeNotifier reappears under src/" m_N1

m_N2() { printf '\n// InMemoryOfferRealtimeNotifier\n' >> "$WORK/src/JeebGateway/Availability/UpstreamPendingOffersStore.cs"; }
control N2 W3.5a A2 "a comment naming InMemoryOfferRealtimeNotifier reappears under src/" m_N2

m_N3() {
  cat > "$WORK/src/JeebGateway/Availability/IOfferRealtimeNotifier.cs" <<'CS'
namespace JeebGateway.Availability;
public interface IOfferRealtimeNotifierResurrected { }
CS
}
control N3 W3.5a A3 "the deleted interface FILE is recreated at its old path" m_N3

# N4 — THE control that matters for W3.5(a). The seam comes back under a name no
# grep in this pack is looking for, is registered, and is injected into the LIVE
# controller. A1/A2/A3 cannot see it. The runtime census can.
m_N4() {
  cat > "$WORK/src/JeebGateway/Availability/OfferEventRealtimeNotifier.cs" <<'CS'
namespace JeebGateway.Availability;

/// A resurrected in-process "realtime" recorder under a different name.
public interface IOfferEventRealtimeNotifier
{
    Task NotifyNewOfferAsync(string clientId, string offerId, CancellationToken ct);
}

public sealed class OfferEventRealtimeNotifier : IOfferEventRealtimeNotifier
{
    private readonly List<string> _events = new();
    public Task NotifyNewOfferAsync(string clientId, string offerId, CancellationToken ct)
    {
        lock (_events) { _events.Add($"{clientId}:{offerId}"); }
        return Task.CompletedTask;
    }
}
CS
  py <<'PY' "$WORK/src/JeebGateway/Program.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
anchor="// Auto-offline notifications flow through the shared push pipeline"
s=s.replace(anchor,
 "builder.Services.AddSingleton<JeebGateway.Availability.IOfferEventRealtimeNotifier,"
 " JeebGateway.Availability.OfferEventRealtimeNotifier>();\n\n"+anchor,1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N4 W3.5a A5_A8 "the deleted seam is re-added under a DIFFERENT name and registered (A1's grep cannot see it)" m_N4 A1

# N5 — ANTI-OVER-DELETION. Removes the CUSTOMER's real notification while leaving
# every "the realtime seam is gone" assertion green. This is the failure mode a
# delete-only pack cannot detect.
m_N5() {
  py <<'PY' "$WORK/src/JeebGateway/Controllers/RequestOffersController.cs"
import io,sys,re
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
# neuter the push dispatch call site without touching the DI registration
s=s.replace(".NotifyNewOfferAsync(pushContext, pushClientId, requestId, pushOfferId, pushFee, token));",
            ".NotifyNewOfferAsync(pushContext, \"\", requestId, pushOfferId, pushFee, token));",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N5 W3.5a A5_A8 "the CUSTOMER's real push recipient is broken (delete-not-wire deleted the wrong thing)" m_N5 A1

# The re-asserted claim must land in its OWN block. Inserting it into the class doc
# (as this control first did) puts it inside the writer's GW3-marked correction,
# where false-claim-scan.py exempts it BY DESIGN — the control then reports "did not
# red" for a reason that is the harness's fault, and proves nothing. A blank line
# before it makes it a separate, unmarked block. This is the history-marker mechanism
# working correctly and a control written carelessly against it.
m_N6() {
  printf '\n// Realtime "new offer" event to the Client on every accepted submission.\n' \
    >> "$WORK/src/JeebGateway/Controllers/RequestOffersController.cs"
}
control N6 W3.5a A9 "the retired 'realtime WS event to the Client' doc claim is re-asserted as live" m_N6

# =============================================================================
# ITEM W3.5c — the store, the local accept, the false comment
# =============================================================================
m_N7() { printf '\n// InMemoryPendingOffersStore\n' >> "$WORK/src/JeebGateway/Availability/UpstreamPendingOffersStore.cs"; }
control N7 W3.5c C1 "a comment naming InMemoryPendingOffersStore reappears under src/" m_N7

m_N8() { printf '\n// AcceptInMemoryAsync\n' >> "$WORK/src/JeebGateway/Controllers/V1/JeebOffersController.cs"; }
control N8 W3.5c C2 "a comment naming AcceptInMemoryAsync reappears under src/" m_N8

# N9 — the anti-OVER-deletion leg for W3.5(c): deleting BOTH accept paths satisfies
# "AcceptInMemoryAsync == 0" perfectly.
m_N9() {
  py <<'PY' "$WORK/src/JeebGateway/Controllers/V1/JeebOffersController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
io.open(p,"w",encoding="utf-8").write(s.replace("AcceptUpstreamAsync","AcceptForwardAsync"))
PY
}
control N9 W3.5c C2comp "the surviving upstream accept path is renamed away (over-deletion)" m_N9 C2

m_N10() {
  py <<'PY' "$WORK/src/JeebGateway/Availability/UpstreamPendingOffersStore.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.rindex("}")
io.open(p,"w",encoding="utf-8").write(s[:i]+"    internal void EnqueueForTest(string x) { }\n"+s[i:])
PY
}
control N10 W3.5c C3 "a test-only EnqueueForTest( seam is put back into PRODUCTION source" m_N10

# N11 — THE control that matters for W3.5(c). The registration becomes conditional
# again. No symbol grep can see this: the class name is not resurrected, only the
# branch is. C1 must stay green while C4 reds.
m_N11() {
  py <<'PY' "$WORK/src/JeebGateway/Program.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
OLD = """builder.Services.AddSingleton<IPendingOffersStore>(sp =>
    new UpstreamPendingOffersStore(
        sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>(),
        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),"""
NEW = """builder.Services.AddSingleton<IPendingOffersStore>(sp =>
    !sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpstreamFeatureFlags>>().Value.Offer
    ? new UpstreamPendingOffersStore(
        sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>(),
        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
        sp.GetRequiredService<IOfferRequestIndex>(),
        sp.GetRequiredService<JeebGateway.Requests.IRequestsStore>())
    : new UpstreamPendingOffersStore(
        sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>(),
        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),"""
assert OLD in s, "N11 anchor not found"
io.open(p,"w",encoding="utf-8").write(s.replace(OLD, NEW, 1))
PY
}
control N11 W3.5c C4 "the IPendingOffersStore registration becomes CONDITIONAL on the flag again" m_N11 C1

# N12 — V-2's own contract: a concrete injection appears outside the registration.
m_N12() {
  py <<'PY' "$WORK/src/JeebGateway/Controllers/V1/JeebOffersController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("    private readonly IPendingOffersStore _offers;",
            "    private readonly IPendingOffersStore _offers;\n"
            "    private UpstreamPendingOffersStore? _concrete;",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N12 W3.5c C6 "a CONCRETE store type is injected outside the registration (V-2's contract)" m_N12

# N15 — the GW1 cross-batch regression guard. GW3 must not move a sealed number.
m_N15() {
  py <<'PY' "$WORK/src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
OLD = "        typeof(JeebGateway.Availability.IPendingOffersStore),\n    };"
assert OLD in s, "N15 anchor not found"
io.open(p,"w",encoding="utf-8").write(s.replace(OLD, "    };", 1))
PY
}
control N15 W3.5c C8_C13 "the offer ledger is quietly dropped off StoreDurabilityGuard.KnownInMemoryBacklog" m_N15 C1

# N16 — C0: the batch's literal test-pack requirement.
m_N16() { printf '\nthis is not C#\n' >> "$WORK/tests/JeebGateway.IntegrationTests/Fakes/FakeOfferStoreWebApplicationFactory.cs"; }
control N16 W3.5c C0 "the IntegrationTests project stops compiling" m_N16

# -----------------------------------------------------------------------------
# ALREADY-RED assertions: repair -> POS, re-break -> NEG.
# -----------------------------------------------------------------------------

# C7 — the two surviving false claims.
r_C7() {
  py <<'PY' "$WORK/src/JeebGateway/Controllers/V1/JeebOffersController.cs" "$WORK/src/JeebGateway/Availability/UpstreamPendingOffersStore.cs"
import io,sys
a,b=sys.argv[1],sys.argv[2]
s=io.open(a,encoding="utf-8").read()
s=s.replace("/// <c>FeatureFlags:UseUpstream:Offer</c> flag is on; falls back to the\n"
            "/// legacy in-memory store path when the flag is off. The caller is the",
            "/// accept saga unconditionally (GW3 / W3.5(c) deleted the local accept).\n"
            "/// The caller is the",1)
io.open(a,"w",encoding="utf-8").write(s)
t=io.open(b,encoding="utf-8").read()
t=t.replace('"offer-service exposes no get-offer-by-id route; the offer-accept lookup path stays on the " +\n'
            '            "in-memory store until offer-service grows GET /api/v1/offers/{id} (tracked fast-follow).");',
            '"offer-service exposes no get-offer-by-id route; the offer-accept lookup path FAULTS until " +\n'
            '            "offer-service grows GET /api/v1/offers/{id} (JEBV4-148). GW3 deleted the gateway store.");',1)
t=t.replace('"offer-service exposes no bulk withdraw-for-jeeber route; the auto-offline sweeper stays on the " +\n'
            '            "in-memory store until offer-service grows a per-jeeber withdrawal route (tracked fast-follow).");',
            '"offer-service exposes no bulk withdraw-for-jeeber route; the auto-offline sweeper FAULTS until " +\n'
            '            "offer-service grows one (JEBV4-148). GW3 deleted the gateway store.");',1)
io.open(b,"w",encoding="utf-8").write(t)
PY
}
b_C7() {
  py <<'PY' "$WORK/src/JeebGateway/Controllers/V1/JeebOffersController.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace("/// accept saga unconditionally (GW3 / W3.5(c) deleted the local accept).",
            "/// <c>FeatureFlags:UseUpstream:Offer</c> flag is on; falls back to the\n"
            "/// legacy in-memory store path when the flag is off.",1)
io.open(p,"w",encoding="utf-8").write(s)
PY
}
repair_control C7CTL W3.5c C7 "the 2 surviving false in-memory-store claims (repair -> green, re-break -> red)" r_C7 b_C7

# C14 — the 3 confirmed suite regressions. The repair is the one the writer's own
# report says was applied to all 15 files: register the fixture double explicitly.
r_C14() {
  py <<'PY' "$WORK/tests/JeebGateway.IntegrationTests/RequestExpirySweeperTests.cs" "$WORK/tests/JeebGateway.IntegrationTests/ClientVisibilityAndReceiptTests.cs"
import io,sys
a,b=sys.argv[1],sys.argv[2]
s=io.open(a,encoding="utf-8").read()
s=s.replace("                services.AddSingleton<IDurableRequestsMirror, LocalExpiryAuthority>();\n",
            "                services.AddSingleton<IDurableRequestsMirror, LocalExpiryAuthority>();\n"
            "                JeebGateway.IntegrationTests.Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);\n",1)
io.open(a,"w",encoding="utf-8").write(s)
t=io.open(b,encoding="utf-8").read()
t=t.replace("""                if (offersStore is not null)
                {
                    services.RemoveAll<IPendingOffersStore>();
                    services.AddSingleton(offersStore);
                }""",
            """                if (offersStore is not null)
                {
                    services.RemoveAll<IPendingOffersStore>();
                    services.AddSingleton(offersStore);
                }
                else if (!upstreamOffer)
                {
                    Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
                }""",1)
io.open(b,"w",encoding="utf-8").write(t)
PY
}
# C14REPAIR — turns the finding into a checkable claim instead of a paragraph.
# Applies the one-line-per-file fixture repair and asserts the regression list
# shrinks from 3 to EXACTLY 1, and that the survivor is the accept-500 one. If a
# future writer's fix clears all 3 with this repair, this control reds and says so.
if wanted C14REPAIR; then
  c14_filter_real="FullyQualifiedName~AvailabilityEndpointTests|FullyQualifiedName~BR1EnforcementTests|FullyQualifiedName~ClientVisibilityAndReceiptTests|FullyQualifiedName~CreateDoubleSubmitConcurrencyTests|FullyQualifiedName~FlatWithdrawAliasTests|FullyQualifiedName~OfferPushNotifierTests|FullyQualifiedName~OfferStateMachineSm2Tests|FullyQualifiedName~OffersEndpointTests|FullyQualifiedName~PostgresRequestExpiryAuthorityTests|FullyQualifiedName~RequestExpiryObserverTests|FullyQualifiedName~RequestExpirySweeperTests|FullyQualifiedName~RequestOffersEndpointTests|FullyQualifiedName~RequestOffersSeatTests|FullyQualifiedName~RequestExpiryMathParityTests|FullyQualifiedName~S09DeliveryReadScopingTests"
  r_C14; git_sync
  (cd "$WORK" && dotnet build tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj -v q --nologo >/dev/null 2>&1)
  out="$(cd "$WORK" && python3 tests/gw3-pack/lib/suite-differential.py \
          --base origin/main --filter "$c14_filter_real" 2>&1)"
  regs="$(printf '%s\n' "$out" | grep -cE '! REGRESSION')"
  survivor="$(printf '%s\n' "$out" | grep -E '! REGRESSION' | head -1)"
  restore_all
  (cd "$WORK" && dotnet build tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj -v q --nologo >/dev/null 2>&1)
  if [ "$regs" = "1" ] && printf '%s' "$survivor" | grep -q 'AfterAccept_OwningClientLists'; then
    echo "OK   C14REPAIR  the fixture repair clears 2 of the 3 regressions; the 3rd is not a fixture bug"
    echo "        -> after repair: 3 regressions -> $regs"
    echo "        -> survivor: $(printf '%s' "$survivor" | sed 's/^ *//')"
    echo "           (it now fails on acceptResp.StatusCode == InternalServerError, one line"
    echo "            PAST the store: flag-off accept dials the real OfferServiceClient)"
    BEHAVED=$((BEHAVED+1))
  else
    echo "BAD  C14REPAIR  [regressions after repair = $regs, expected 1]"
    printf '%s\n' "$out" | grep -E '! REGRESSION|VERDICT' | sed 's/^/        | /'
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("C14REPAIR")
  fi
fi

# C14's remaining controls are on the INSTRUMENT, not on the batch's three reds, and here is
# why that changed. The first attempt was a repair_control: repair the 3 regressions
# in the export, watch C14 go green. Two of the three DO repair with one line each
# (RequestExpirySweeperTests and ClientVisibilityAndReceiptTests just never got the
# fixture store). The third does not, and the reason is the finding:
#
#   ClientVisibilityAndReceiptTests.AfterAccept_OwningClientLists_... runs with
#   UseUpstream:Offer=false and NO offer-service stub. Registering the fixture store
#   fixes its SUBMIT (verified: the InvalidCastException / no-BaseAddress dial both
#   go away) and the test then fails one line later with `acceptResp.StatusCode ...
#   found InternalServerError`. Before GW3, flag-off ran the LOCAL accept against the
#   gateway's own store and returned 200. GW3 made accept unconditional, so flag-off
#   now dials the real OfferServiceClient, which has no BaseAddress under test.
#   That is not a fixture oversight — it is the writer's own stated CONSEQUENCE
#   ("off + no test override = the offer surface is not functional") showing up as a
#   test that cannot be repaired by registering a store.
#
# So C14 gets two controls that ARE satisfiable, and neither pretends the third red
# is repairable:
#   C14POS  the differential returns PASS on a filter GW3 did not touch  -> it can
#           go green, so the red on the real filter means something.
#   C14NEG  break a test that is green at the base; the differential must name THAT
#           test as a REGRESSION -> it can go red on an injected fault, not only on
#           the three that happen to be there.
c14_filter_clean="FullyQualifiedName~OfferAcceptUpstreamTests|FullyQualifiedName~OfferMutationEndpointTests|FullyQualifiedName~JeeberMyOffersFlatTests"

if wanted C14POS; then
  out="$(cd "$WORK" && python3 tests/gw3-pack/lib/suite-differential.py \
          --base origin/main --filter "$c14_filter_clean" 2>&1)"; rc=$?
  line="$(printf '%s\n' "$out" | grep -E '^VERDICT' | head -1)"
  if [ $rc -eq 0 ] && printf '%s\n' "$out" | grep -q 'no test that passes at the base fails'; then
    echo "OK   C14POS  the differential returns PASS on a filter GW3 did not touch"
    echo "        -> exit 0: $line"
    BEHAVED=$((BEHAVED+1))
  else
    echo "BAD  C14POS  [exit $rc] $line"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("C14POS")
  fi
fi

if wanted C14NEG; then
  python3 - "$WORK/tests/JeebGateway.IntegrationTests/JeeberMyOffersFlatTests.cs" <<'PYX'
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
i=s.index("[Fact]")
j=s.index("{", s.index("public async Task", i))
io.open(p,"w",encoding="utf-8").write(
    s[:j+1] + '\n        throw new System.Exception("GW3 C14NEG injected regression");\n' + s[j+1:])
PYX
  git_sync
  (cd "$WORK" && dotnet build tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj -v q --nologo >/dev/null 2>&1)
  out="$(cd "$WORK" && python3 tests/gw3-pack/lib/suite-differential.py \
          --base origin/main --filter "$c14_filter_clean" 2>&1)"; rc=$?
  named="$(printf '%s\n' "$out" | grep -E '! REGRESSION' | head -1)"
  restore_all
  (cd "$WORK" && dotnet build tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj -v q --nologo >/dev/null 2>&1)
  if [ $rc -ne 0 ] && [ -n "$named" ]; then
    echo "OK   C14NEG  an injected fault in a base-green test is named as a REGRESSION"
    echo "        -> exit $rc, $(printf '%s' "$named" | sed 's/^ *//')"
    BEHAVED=$((BEHAVED+1))
  else
    echo "BAD  C14NEG  [exit $rc] the differential did not name the injected regression"
    MISBEHAVED=$((MISBEHAVED+1)); MISBEHAVED_IDS+=("C14NEG")
  fi
fi

# =============================================================================
# ITEM HYGIENE
# =============================================================================
m_N17() {
  (cd "$WORK" && git -c user.email=pack@local -c user.name=pack commit -q --allow-empty \
      -m "GW5: a foreign-batch commit lands on the GW3 branch" >/dev/null)
}
u_N17() { (cd "$WORK" && git reset -q --hard HEAD~1 >/dev/null 2>&1); }
control N17 HYGIENE H1 "a GW5-tagged commit appears in origin/main..HEAD" m_N17 "" u_N17

m_N18() { mkdir -p "$WORK/.github/workflows" && printf 'name: x\non: push\n' > "$WORK/.github/workflows/gw3-control.yml"; }
u_N18() { rm -f "$WORK/.github/workflows/gw3-control.yml"; }
control N18 HYGIENE H2 "a .github/ workflow file is touched (owner exclusion 2)" m_N18 "" u_N18

m_N19() { printf '\n// var x = "http://192.168.2.50:10040";\n' >> "$WORK/src/JeebGateway/Program.cs"
          py <<'PY' "$WORK/src/JeebGateway/Program.cs"
import io,sys
p=sys.argv[1]; s=io.open(p,encoding="utf-8").read()
s=s.replace('// var x = "http://192.168.2.50:10040";','var _no50 = "http://192.168.2.50:10040";')
io.open(p,"w",encoding="utf-8").write(s)
PY
}
control N19 HYGIENE H3 "a LIVE http://192.168.2.50 reference is added by the branch" m_N19

# =============================================================================
echo
echo "controls behaved   : $BEHAVED"
echo "controls MISbehaved: $MISBEHAVED ${MISBEHAVED_IDS[*]:-}"
echo "controls skipped   : $SKIPPED"
exit $MISBEHAVED
