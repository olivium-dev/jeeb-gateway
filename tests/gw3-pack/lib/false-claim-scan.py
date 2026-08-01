#!/usr/bin/env python3
"""
GW3 — instrument for the FALSE-COMMENT half of the batch.

GW3's headline is not a delete, it is a LIE in the source: Program.cs claimed the
gateway's in-memory offer store was "KEPT registered either way so … the
auto-offline sweeper / accept-lookup paths … continue to resolve it directly".
They never did — they take the interface, so under the flag every deployed overlay
actually sets they resolved the UPSTREAM store and faulted. A comment that names a
defence which does not exist retires the question, so it is worse than no comment.

This scanner asserts that the same class of statement does not SURVIVE anywhere in
src/ after the batch.

--- THE TWO THINGS THAT MAKE THIS INSTRUMENT NON-TRIVIAL -------------------------

1. A CORRECTION QUOTES THE THING IT CORRECTS. The GW3 writer's own fix quotes the
   false sentence verbatim before demolishing it. A naive grep therefore reds on
   correct work. So a match is only LIVE when its enclosing block carries no
   history marker — the same "dated as history, never deleted" mechanism DT0's T9
   uses (GATE.md §7, OWNER-DECISIONS.md 2026-07-31 10:24Z). Block = the maximal
   blank-line-delimited run of lines containing the match.

2. THE CATALOGUE IS HAND-AUDITED, NOT A FUZZY REGEX. A broad pattern like
   "in-memory" reds on ~40 legitimate lines (other stores, the interface's own
   semantics doc, OffersController's genuinely-still-live flag-off branches) —
   that is the "wrong-but-well-formed predicate" GATE.md §6.5 warns about. Each
   entry below was read first-hand at HEAD and at origin/main, and carries the
   reason it is false and the code that makes it false.

3. THE CATALOGUE IS CLOSED, SO ITS LABEL MUST NOT BE UNIVERSAL. This scanner cannot
   prove "no false in-memory-offer-store claim survives in src/" — it can only prove
   "none of the N sentences below survives". That gap is not theoretical: GW3's own
   independent verifier found THREE more stale statements in the very files this
   scanner targets, and every one of them slipped past a clean run because no
   catalogue entry described it. FC5/FC6/FC7 (2026-08-01) are those three. When you
   find another, ADD IT — a clean report from a closed catalogue is a statement about
   the catalogue, not about the tree.

--- WHAT MAKES A "CLEAN" REPORT TRUSTWORTHY -------------------------------------

A scanner that reports zero offenders has two indistinguishable explanations: the
tree is clean, or the pattern stopped matching. Three mechanisms separate them, and
the first two run on EVERY invocation:

  a. PATTERN SELF-TEST (always, cannot be skipped). Each entry carries `probe` — a
     verbatim specimen of the sentence it forbids — and `miss` — a near-miss that
     must NOT match. A pattern that fails either is a HARD ERROR (exit 2), never a
     clean report. This is the control that survives history moving.
  b. TARGET-FILE EXISTENCE (always). A renamed/deleted target file reports LIVE,
     not clean.
  c. GIT POSITIVE CONTROL (--controls). Each entry names how many live matches it
     expects at PINNED_BASE, a commit whose content can never change.

     PINNED_BASE was `origin/main` until 2026-08-01. That was a MOVING ref: once GW3
     merged, every base expectation in this file (1, 2, 1, 1) became 0 and all four
     positive controls reported BAD — the instrument that proves the instrument works
     had been silently red since the batch it was written for landed. Pin it, or the
     control expires the moment the work ships.

Usage:
  false-claim-scan.py --ref HEAD                  # report, exit 1 if any live claim
  false-claim-scan.py --ref HEAD --item W3.5c     # one member item only
  false-claim-scan.py --controls                  # also assert counts at PINNED_BASE
  false-claim-scan.py --ref HEAD --json
"""
import argparse
import json
import re
import subprocess
import sys

HISTORY_MARKER = re.compile(
    r"GW3|W3\.5|used to (?:say|read|register|be)|no longer|never did|never was|"
    r"was deleted|is DELETED|superseded|historical record|kept, not deleted",
    re.I,
)

# The base ref every git positive control is measured against. c3d5451 is GW5, the
# COMMIT PARENT of GW3 (2a3a01d) — the last tree in which FC1..FC4 were all live.
# It is an ancestor of origin/main and its content is immutable, which is the whole
# point: see "WHAT MAKES A CLEAN REPORT TRUSTWORTHY" (c) above. Overridable with
# --base for a one-off, but do not make it a branch name again.
PINNED_BASE = "c3d5451"

# id, member item, target file, pattern, why it is false, expectation at HEAD,
# expectation at PINNED_BASE, and the two self-test specimens:
#   probe — a verbatim sentence the pattern MUST match (proves it still matches)
#   miss  — a near-miss the pattern MUST NOT match (proves it is not a catch-all)
CATALOGUE = [
    dict(
        id="FC1",
        item="W3.5c",
        file="src/JeebGateway/Controllers/V1/JeebOffersController.cs",
        pattern=r"falls back to the\s*(?:///)?\s*legacy in-memory store path when the flag is off",
        why=("JeebOffersController's CLASS doc still advertises a flag-off local accept. "
             "Accept() is unconditional since GW3 (`return await AcceptUpstreamAsync(...)`, "
             "one call, no branch) and AcceptInMemoryAsync is deleted. The writer corrected "
             "the METHOD doc 80 lines below and left the class doc asserting the opposite."),
        probe="/// falls back to the\n/// legacy in-memory store path when the flag is off.",
        miss="/// falls back to the legacy upstream path when the flag is off.",
        head=0,
        base=1,
    ),
    dict(
        id="FC2",
        item="W3.5c",
        file="src/JeebGateway/Availability/UpstreamPendingOffersStore.cs",
        pattern=r"stay(?:s)? on the[\s\"+]*in-memory store",
        why=("Two NotSupportedException messages (GetAsync, WithdrawForJeeberAsync) still tell "
             "whoever reads the production stack trace that the offer-accept lookup and the "
             "auto-offline sweeper 'stay on the in-memory store'. There is no in-memory store "
             "to stay on — it is in the test project. This is the SAME sentence the writer's "
             "own new type doc calls out 180 lines above: \"The old note here claimed those "
             "paths 'stay on the in-memory store' - they never did\"."),
        probe="\"the offer-accept lookup and the sweeper stay on the \" +\n\"in-memory store\"",
        miss="\"the offer-accept lookup and the sweeper stay on the upstream store\"",
        head=0,
        base=2,
    ),
    dict(
        id="FC3",
        item="W3.5c",
        file="src/JeebGateway/Program.cs",
        pattern=r"continue to resolve it directly",
        why=("THE comment the batch names (GW3.md: 'fixes the false comment at "
             "Program.cs:2143-2145'). At PINNED_BASE it is a live claim; at HEAD it survives "
             "only inside the writer's GW3-marked correction block, quoted as history. "
             "POSITIVE CONTROL: this claim MOVES between the two refs, so a scanner that "
             "reports 0 at HEAD and 0 at base is broken, not clean."),
        probe="// the accept-lookup paths continue to resolve it directly",
        miss="// the accept-lookup paths continue to resolve the interface",
        head=0,
        base=1,
    ),
    dict(
        id="FC4",
        item="W3.5a",
        file="src/JeebGateway/Controllers/RequestOffersController.cs",
        pattern=r'Realtime "new offer" event to the Client',
        why=("RequestOffersController's doc listed a realtime WS event to the Client as an "
             "ENFORCED acceptance criterion of the submit endpoint. No such event ever "
             "reached a client: the notifier appended to an in-process List<T> with no "
             "reader. This is the exact statement W3.5(a) exists to retire. "
             "POSITIVE CONTROL: live at PINNED_BASE, absent at HEAD."),
        probe="///   <item>Realtime \"new offer\" event to the Client.</item>",
        miss="///   <item>Realtime \"new offer\" event to the Jeeber.</item>",
        head=0,
        base=1,
    ),

    # ---------------------------------------------------------------------------
    # FC5..FC7 — added 2026-08-01, AFTER GW3 merged and after this scanner had
    # reported a clean HEAD. GW3's independent verifier read the same files by hand
    # and found three more stale statements; none of FC1..FC4 could match any of
    # them. They are here because "the scan was green" was, for these three, a fact
    # about the catalogue.
    # ---------------------------------------------------------------------------
    dict(
        id="FC5",
        item="W3.5c",
        file="src/JeebGateway/Availability/UpstreamPendingOffersStore.cs",
        pattern=r"Keep FeatureFlags:UseUpstream:Offer OFF",
        why=("AcceptAsync's NotSupportedException MESSAGE — the text an on-call reader takes "
             "off a production stack trace — told them to hold the offer upstream flag OFF for "
             "the accept path 'until OffersController is migrated'. FALSE TWICE. (1) It is "
             "migrated: OffersController.Accept opens with `if (_flags.Offer) return await "
             "AcceptViaUpstreamAsync(offerId, actorId, ct);`. (2) Following the advice breaks "
             "the product — appsettings.json's own _comment_offer_gw3 states 'Off + no test "
             "override = the offer surface is not functional', because GW3 deleted the "
             "in-memory store the flag-off branch used to resolve. An exception message that "
             "tells you to disable a working feature is worse than a silent throw."),
        probe="\"not the IPendingOffersStore.AcceptAsync seam. Keep FeatureFlags:UseUpstream:Offer OFF for the \" +",
        miss="\"every deployed overlay sets FeatureFlags:UseUpstream:Offer ON\"",
        head=0,
        base=1,
    ),
    dict(
        id="FC6",
        item="W3.5c",
        file="src/JeebGateway/Availability/UpstreamPendingOffersStore.cs",
        pattern=r"supersede-aware in-memory[\s\"+/]*accept",
        why=("AcceptWithSupersedeAsync's NotSupportedException message ended 'The supersede-aware "
             "in-memory accept is the flag-OFF path only.' — pointing the reader at a fallback "
             "that ships in no binary. GW3 deleted the gateway's in-memory offer store; the only "
             "IPendingOffersStore implementation left in src/ is UpstreamPendingOffersStore "
             "itself, so the flag-off branch resolves straight back to these throws. The "
             "supersede-aware implementation survives only as a test double "
             "(tests/JeebGateway.IntegrationTests/Fakes/FakePendingOffersStore.cs)."),
        probe="\"NOT the IPendingOffersStore seam. The supersede-aware in-memory accept is the flag-OFF path only.\");",
        miss="\"NOT the IPendingOffersStore seam. The supersede-aware upstream accept is the only accept.\");",
        head=0,
        base=1,
    ),
    dict(
        id="FC7",
        item="W3.5c",
        file="src/JeebGateway/Controllers/V1/JeebOffersController.cs",
        pattern=r"In-memory accept path \(legacy / test-only;[\s/]*flag off\)",
        why=("A SECTION BANNER, not a sentence — which is why a prose-shaped catalogue missed "
             "it. 'In-memory accept path (legacy / test-only; flag off)' was accurate until GW3 "
             "deleted the ~95-line local accept helper it headed. What sits under it now is "
             "ResolveAcceptedFeeAsync — called from BuildAcceptedResponseAsync on the UPSTREAM "
             "accept path — plus the deletion tombstone and the response mapper. Labelling live "
             "upstream code 'test-only, flag off' is the specific failure mode this whole "
             "instrument exists for: a note that retires the question. "
             "NOTE ON ITS BASE CONTROL: unlike FC1..FC6 this string was TRUE at PINNED_BASE "
             "(the helper still existed). base=1 here is a pattern-liveness control — proof the "
             "regex still sees the tree — not a claim that it was false at that ref."),
        probe="    // In-memory accept path (legacy / test-only; flag off)",
        miss="    // Accept-response helpers  (upstream path — see AcceptUpstreamAsync)",
        head=0,
        base=1,
    ),
    # FC8 — added 2026-08-01 by the durability-guard follow-up, AFTER FC5..FC7. Found by
    # a reviewer reading JeebOffersController top-to-bottom rather than by any pattern:
    # none of FC1..FC7 could match it, because it is a SUBORDINATE CLAUSE inside a comment
    # about something else (handover-code scoping), not a standalone statement about the
    # in-memory path. Same lesson as FC7's banner, one level smaller: the catalogue's shape
    # assumptions, not the tree, decide what it can see.
    dict(
        id="FC8",
        item="W3.5c",
        file="src/JeebGateway/Controllers/V1/JeebOffersController.cs",
        pattern=r"the in-memory path checks ClientId",
        why=("A dependent clause inside the Gap-G4 handover-code comment cited 'the in-memory "
             "path checks ClientId' as one of the TWO reasons the accept response is "
             "owner-scoped by construction. That path is gone — AcceptInMemoryAsync has zero "
             "hits in this file since GW3 — so half of a stated safety argument referred to "
             "code that does not exist. The remaining half (offer-service returns NotOwner -> "
             "403 before Accepted is ever reached) is load-bearing on its own, which is what "
             "makes this dangerous rather than merely stale: the sentence still reads as if "
             "the guarantee were belt-and-braces when it now rests on a single mechanism. "
             "NOTE ON ITS BASE CONTROL: like FC7 this string was TRUE at PINNED_BASE (the "
             "in-memory accept still existed and did check ClientId). base=1 is a "
             "pattern-liveness control, not a claim that it was false at that ref."),
        probe="// ever reaching Accepted here, and the in-memory path checks ClientId), so this",
        miss="// ever reaching Accepted here, and the upstream path checks ClientId), so this",
        head=0,
        base=1,
    ),
]


def sh(*args):
    p = subprocess.run(args, capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def blob(ref, path):
    rc, out, err = sh("git", "show", f"{ref}:{path}")
    if rc != 0:
        return None
    return out


def blocks(text):
    """Maximal blank-line-delimited runs of lines: [(first_line_no, text)]."""
    out, cur, first = [], [], None
    for i, line in enumerate(text.split("\n"), 1):
        if line.strip() == "":
            if cur:
                out.append((first, "\n".join(cur)))
                cur, first = [], None
        else:
            if not cur:
                first = i
            cur.append(line)
    if cur:
        out.append((first, "\n".join(cur)))
    return out


def self_test(entries):
    """
    PATTERN SELF-TEST — runs on EVERY invocation and cannot be skipped.

    A closed catalogue reporting "0 offenders" is only meaningful if each pattern
    still matches the sentence it was written for. Assert that directly, against
    literal specimens carried in the entry, with no git involved:

      probe -> MUST match  (a pattern that stopped matching cannot report clean)
      miss  -> MUST NOT match (a pattern that matches everything proves nothing)

    A failure here is a HARD ERROR (exit 2), deliberately distinct from exit 1
    "a live false claim survives": the instrument is broken, so its verdict on the
    tree — clean or otherwise — carries no information at all.
    """
    failures = []
    for e in entries:
        for field in ("probe", "miss"):
            if field not in e:
                failures.append(f"{e['id']}: catalogue entry has no `{field}` specimen")
        if "probe" not in e or "miss" not in e:
            continue
        pat = re.compile(e["pattern"], re.S)
        if not pat.search(e["probe"]):
            failures.append(
                f"{e['id']}: pattern no longer matches its own probe — it would report "
                f"CLEAN for a tree that still contains the claim.\n"
                f"        pattern: {e['pattern']}\n"
                f"        probe  : {e['probe']!r}")
        if pat.search(e["miss"]):
            failures.append(
                f"{e['id']}: pattern matches its near-miss specimen — it is a catch-all, "
                f"so a red report would not mean the claim is present.\n"
                f"        pattern: {e['pattern']}\n"
                f"        miss   : {e['miss']!r}")
    return failures


def scan_one(ref, entry):
    text = blob(ref, entry["file"])
    if text is None:
        # A missing target file is a silent-zero: it would report "clean". Refuse.
        return None, [{"line": 0, "live": True,
                       "text": f"TARGET FILE ABSENT at {ref}: {entry['file']}"}]
    pat = re.compile(entry["pattern"], re.S)
    live, history = [], []
    for first, btxt in blocks(text):
        marked = bool(HISTORY_MARKER.search(btxt))
        for m in pat.finditer(btxt):
            line = first + btxt[:m.start()].count("\n")
            rec = {"line": line, "live": not marked,
                   "text": text.split("\n")[line - 1].strip()[:150]}
            (history if marked else live).append(rec)
    return live, history


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", default="HEAD")
    ap.add_argument("--base", default=PINNED_BASE,
                    help=f"git positive-control ref (default: {PINNED_BASE}, pinned on purpose)")
    ap.add_argument("--item", default=None, help="W3.5a | W3.5c")
    ap.add_argument("--controls", action="store_true",
                    help="also assert each claim's expected count at --base")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    entries = [e for e in CATALOGUE if a.item is None or e["item"] == a.item]
    if not entries:
        print(f"no catalogue entry for item {a.item}", file=sys.stderr)
        return 2

    # Before reading a single byte of the tree: prove the patterns still work.
    st = self_test(entries)
    if st:
        print("SELF-TEST FAILED — this scanner's verdict is meaningless, not clean:",
              file=sys.stderr)
        for f in st:
            print(f"  {f}", file=sys.stderr)
        return 2

    rc = 0
    report = []
    for e in entries:
        live, hist = scan_one(a.ref, e)
        row = {"id": e["id"], "item": e["item"], "file": e["file"],
               "ref": a.ref, "live": live, "history_exempt": hist,
               "expected_live": e["head"]}
        ok = live is not None and len(live) == e["head"]
        row["ok"] = ok
        if not ok:
            rc = 1
        if a.controls:
            blive, bhist = scan_one(a.base, e)
            row["base"] = {"ref": a.base, "live": blive, "history_exempt": bhist,
                           "expected_live": e["base"]}
            bok = blive is not None and len(blive) == e["base"]
            row["base"]["ok"] = bok
            if not bok:
                rc = 1
        report.append(row)

    if a.json:
        print(json.dumps(report, indent=2))
        return rc

    for row in report:
        e = next(x for x in CATALOGUE if x["id"] == row["id"])
        n = len(row["live"]) if row["live"] is not None else "?"
        mark = "ok " if row["ok"] else "BAD"
        print(f"{mark} {row['id']} [{row['item']}] {row['file']}")
        print(f"      live claims at {a.ref}: {n} (expected {row['expected_live']})")
        for h in (row["live"] or []):
            print(f"        LIVE  :{h['line']}  {h['text']}")
        for h in row["history_exempt"]:
            print(f"        (hist) :{h['line']}  {h['text'][:110]}")
        if not row["ok"]:
            print(f"      WHY THIS IS FALSE: {e['why']}")
        if "base" in row:
            b = row["base"]
            bn = len(b["live"]) if b["live"] is not None else "?"
            bmark = "ok " if b["ok"] else "BAD"
            print(f"      {bmark} POS CONTROL at {b['ref']}: {bn} live "
                  f"(expected {b['expected_live']})")
            for h in (b["live"] or []):
                print(f"        LIVE  :{h['line']}  {h['text'][:110]}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
