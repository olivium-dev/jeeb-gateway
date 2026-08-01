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

Every claim reports a HEAD expectation and an origin/main expectation. Two of the
four are POSITIVE controls that MOVE (live at the base, gone at the head), which is
what proves the scanner is looking at the tree rather than at nothing.

Usage:
  false-claim-scan.py --ref HEAD                  # report, exit 1 if any live claim
  false-claim-scan.py --ref HEAD --item W3.5c     # one member item only
  false-claim-scan.py --controls                  # run HEAD + origin/main expectations
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

# id, member item, target file, pattern, why it is false, expectation at HEAD,
# expectation at origin/main.
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
        head=0,
        base=2,
    ),
    dict(
        id="FC3",
        item="W3.5c",
        file="src/JeebGateway/Program.cs",
        pattern=r"continue to resolve it directly",
        why=("THE comment the batch names (GW3.md: 'fixes the false comment at "
             "Program.cs:2143-2145'). At origin/main it is a live claim; at HEAD it survives "
             "only inside the writer's GW3-marked correction block, quoted as history. "
             "POSITIVE CONTROL: this claim MOVES between the two refs, so a scanner that "
             "reports 0 at HEAD and 0 at base is broken, not clean."),
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
             "POSITIVE CONTROL: live at origin/main, absent at HEAD."),
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
    ap.add_argument("--base", default="origin/main")
    ap.add_argument("--item", default=None, help="W3.5a | W3.5c")
    ap.add_argument("--controls", action="store_true",
                    help="also assert each claim's expected count at --base")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    entries = [e for e in CATALOGUE if a.item is None or e["item"] == a.item]
    if not entries:
        print(f"no catalogue entry for item {a.item}", file=sys.stderr)
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
