#!/usr/bin/env python3
"""
GW1 test pack — INDEPENDENT measurement of StoreDurabilityGuard's three buckets.

Why this exists rather than a `.Should().HaveCount(33)` assertion in the suite.
The writer knew the sealed number (SEALED-PREDICATES.md §4, GW1-1 = 33) before
writing the test, so an in-suite `HaveCount(33)` is a TRIPWIRE, not evidence: it
reports the number the writer chose to write down. This parser reads the C# source
directly, at an arbitrary git ref, with no reference to the sealed value, so the
base->head DELTA can be computed rather than asserted.

The delta is the load-bearing measurement. "Critical.Length == 33" can be reached
from 32 by adding ANY row — including a junk one, or by swapping an existing row
out and two in. What GW1 claims is narrower and checkable:

    Critical(HEAD) \\ Critical(origin/main) == { ISettlementLedgerClient }
    Critical(origin/main) \\ Critical(HEAD)  == {}

Usage:
    critical-parse.py --ref HEAD                    # human-readable
    critical-parse.py --ref origin/main --json      # machine-readable
    critical-parse.py --delta origin/main HEAD      # set difference, exit 0/1

Exit codes: 0 = parsed (or delta matched), 1 = parse failure / delta mismatch.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys

GUARD = "src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs"


def read_at_ref(ref: str, path: str = GUARD) -> str:
    """`git show <ref>:<path>`; ref='' or 'WORKTREE' reads the working tree."""
    if ref in ("", "WORKTREE"):
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()
    out = subprocess.run(
        ["git", "show", f"{ref}:{path}"],
        capture_output=True, text=True, check=False,
    )
    if out.returncode != 0:
        raise SystemExit(f"FATAL: cannot read {path} at ref '{ref}': {out.stderr.strip()}")
    return out.stdout


def strip_comments(src: str) -> str:
    """
    Remove // line comments and /* */ blocks. Necessary and load-bearing: the
    Critical block is ~90 lines of prose rationale interleaved with the tuples,
    and the prose mentions type names. A regex over the raw text would count a
    commented-out row as live — which is exactly the mistake a durability guard
    must not make.
    """
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def _block(src: str, decl_regex: str) -> str:
    """Text between the `= {` that follows a declaration and its matching `};`."""
    m = re.search(decl_regex, src)
    if not m:
        raise SystemExit(f"FATAL: declaration not found: {decl_regex}")
    start = src.index("{", m.end() - 1)
    depth, i = 0, start
    while i < len(src):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[start + 1:i]
        i += 1
    raise SystemExit(f"FATAL: unbalanced braces after {decl_regex}")


def short(fqn: str) -> str:
    return fqn.rsplit(".", 1)[-1]


def parse(src: str) -> dict:
    clean = strip_comments(src)

    crit_block = _block(clean, r"\[\]\s+Critical\s*=")
    # (typeof(A.B.IFoo), new[] { typeof(A.B.Foo), typeof(A.B.Bar) }),
    tuple_re = re.compile(
        r"\(\s*typeof\(\s*([\w.]+)\s*\)\s*,\s*new\[\]\s*\{(.*?)\}\s*\)", re.S)
    critical = []
    for iface, impls in tuple_re.findall(crit_block):
        impl_names = re.findall(r"typeof\(\s*([\w.]+)\s*\)", impls)
        critical.append({"iface": iface, "iface_short": short(iface),
                         "impls": impl_names,
                         "impls_short": [short(i) for i in impl_names]})

    def flat(decl: str) -> list[str]:
        return re.findall(r"typeof\(\s*([\w.]+)\s*\)", _block(clean, decl))

    intentional = flat(r"Type\[\]\s+IntentionalInMemory\s*=")
    backlog = flat(r"Type\[\]\s+KnownInMemoryBacklog\s*=")

    return {
        "critical_count": len(critical),
        "critical": critical,
        "critical_ifaces_short": [c["iface_short"] for c in critical],
        "intentional_in_memory_short": [short(t) for t in intentional],
        "known_in_memory_backlog_short": [short(t) for t in backlog],
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", default="HEAD")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--delta", nargs=2, metavar=("BASE", "HEAD"),
                    help="print the Critical set difference between two refs")
    ap.add_argument("--expect-added", default=None,
                    help="with --delta: the ONLY interface short-name that may be added")
    args = ap.parse_args()

    if args.delta:
        base_ref, head_ref = args.delta
        base = parse(read_at_ref(base_ref))
        head = parse(read_at_ref(head_ref))
        b = set(base["critical_ifaces_short"])
        h = set(head["critical_ifaces_short"])
        added, removed = sorted(h - b), sorted(b - h)
        print(f"base ref            : {base_ref}")
        print(f"head ref            : {head_ref}")
        print(f"Critical.Length base: {base['critical_count']}")
        print(f"Critical.Length head: {head['critical_count']}")
        print(f"added               : {added}")
        print(f"removed             : {removed}")
        ok = True
        if removed:
            print("MISMATCH: a pre-existing Critical store was REMOVED")
            ok = False
        if args.expect_added is not None:
            if added != [args.expect_added]:
                print(f"MISMATCH: expected exactly ['{args.expect_added}'] added, got {added}")
                ok = False
            else:
                impls = next(c["impls_short"] for c in head["critical"]
                             if c["iface_short"] == args.expect_added)
                print(f"durable impls for {args.expect_added}: {impls}")
        return 0 if ok else 1

    data = parse(read_at_ref(args.ref))
    if args.json:
        print(json.dumps(data, indent=2))
    else:
        print(f"ref                    : {args.ref}")
        print(f"Critical.Length        : {data['critical_count']}")
        print(f"IntentionalInMemory    : {data['intentional_in_memory_short']}")
        print(f"KnownInMemoryBacklog   : {data['known_in_memory_backlog_short']}")
        for c in data["critical"]:
            print(f"  {c['iface_short']:<42} -> {', '.join(c['impls_short'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
