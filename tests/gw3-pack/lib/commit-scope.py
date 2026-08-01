#!/usr/bin/env python3
"""
GW3 — anti-contamination instrument.

GW3's V-1 contract is `git log --oneline origin/main..HEAD` contains no GW5 commit.
That contract has a hole worth naming: GW5 was MERGED to origin/main before GW3
branched, so the range is empty of GW5 commits no matter what the writer did. A
check that cannot fail is not evidence (GATE.md §7), so this instrument does two
things instead of one:

  1. FOREIGN-BATCH SCAN — every commit in <base>..HEAD must be tagged for THIS
     batch and must not name another batch. Reported per commit.
  2. THE SCANNER'S OWN POSITIVE CONTROL — the same matcher is run over a range
     that IS known to contain a foreign batch commit (by default the two commits
     immediately behind origin/main, which are GW5 and GW1). If the matcher does
     not find them there, it is blind and its clean answer on the real range means
     nothing.

Usage:
  commit-scope.py --base origin/main --head HEAD --batch GW3
  commit-scope.py --base origin/main --head HEAD --batch GW3 --pos-range origin/main~2..origin/main
Exit 0 when the real range is clean AND the positive control found something.
"""
import argparse
import re
import subprocess
import sys

# Every batch id used by the b05 delivery plan, so "a commit belonging to another
# batch" is a decidable question rather than a vibe.
BATCH_IDS = ["DT0", "MB1", "MB2", "MB3", "CB1", "CB2", "CB3", "CB4", "CB5",
             "GW1", "GW2", "GW3", "GW4", "GW5", "OD1", "OPS1a", "OPS1b",
             "REL1", "RS", "U1", "U2", "U3", "U4", "U5", "U6"]


def sh(*args):
    p = subprocess.run(args, capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def commits(rng):
    rc, out, err = sh("git", "log", "--format=%H%x1f%s", rng)
    if rc != 0:
        print(f"git log {rng} failed: {err.strip()}", file=sys.stderr)
        sys.exit(2)
    rows = []
    for line in out.strip().split("\n"):
        if not line:
            continue
        sha, subject = line.split("\x1f", 1)
        rows.append((sha[:7], subject))
    return rows


def batches_named(subject, mine):
    """Batch ids named by a commit subject, excluding our own."""
    found = set()
    for b in BATCH_IDS:
        if b == mine:
            continue
        if re.search(rf"(?<![A-Za-z0-9]){re.escape(b)}(?![A-Za-z0-9])", subject):
            found.add(b)
    return sorted(found)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="origin/main")
    ap.add_argument("--head", default="HEAD")
    ap.add_argument("--batch", default="GW3")
    ap.add_argument("--pos-range", default=None,
                    help="a range known to contain a FOREIGN batch commit "
                         "(default: <base>~2..<base>)")
    a = ap.parse_args()

    rng = f"{a.base}..{a.head}"
    rows = commits(rng)
    print(f"range               : {rng}")
    print(f"base                : {a.base} = {sh('git','rev-parse',a.base)[1].strip()}")
    print(f"head                : {a.head} = {sh('git','rev-parse',a.head)[1].strip()}")
    print(f"commits in range    : {len(rows)}")

    rc = 0
    if not rows:
        print("REFUSING: 0 commits in range — an empty range is trivially 'clean' and "
              "proves nothing about contamination.", file=sys.stderr)
        rc = 1

    for sha, subject in rows:
        foreign = batches_named(subject, a.batch)
        mine = re.search(rf"(?<![A-Za-z0-9]){a.batch}(?![A-Za-z0-9])", subject) is not None
        tag = "ok " if (not foreign and mine) else "BAD"
        print(f"  {tag} {sha} {subject[:100]}")
        if foreign:
            print(f"        FOREIGN BATCH COMMIT: names {', '.join(foreign)}")
            rc = 1
        if not mine:
            print(f"        UNTAGGED: subject does not name {a.batch}")
            rc = 1

    pos = a.pos_range or f"{a.base}~2..{a.base}"
    prows = commits(pos)
    pfound = [(s, sub, batches_named(sub, a.batch)) for s, sub in prows]
    hits = [p for p in pfound if p[2]]
    print(f"POS control range   : {pos}  ({len(prows)} commit(s))")
    for s, sub, f in pfound:
        print(f"      {s} {sub[:80]}  -> names {f or '<none>'}")
    if not hits:
        print("      POS CONTROL FAILED: the matcher found no foreign batch id in a range "
              "that is supposed to contain one. The clean answer above is not trustworthy.",
              file=sys.stderr)
        rc = 1
    else:
        print(f"      POS CONTROL OK: matcher is not blind "
              f"({len(hits)} foreign-batch commit(s) detected there)")
    return rc


if __name__ == "__main__":
    sys.exit(main())
