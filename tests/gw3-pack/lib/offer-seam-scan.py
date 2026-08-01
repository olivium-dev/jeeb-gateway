#!/usr/bin/env python3
"""
GW3 / W3.5(c) — instrument for the OFFER-STORE SEAM.

Reads C# source at a git REF (never the working tree, so a mutation left only in
the worktree cannot silently pass) and reports three facts about the gateway's
IPendingOffersStore seam:

  1. implementations        — every concrete class under src/ that implements
                              IPendingOffersStore.
  2. registration           — whether Program.cs's IPendingOffersStore mapping is
                              UNCONDITIONAL or branches on a feature flag.
  3. concrete_uses          — every occurrence of a CONCRETE store type in src/
                              outside (a) its own declaring file and (b) the
                              Program.cs registration expression.

Fact 3 is the machine form of GW3's V-2 contract, "0 concrete injections outside
the registration". Fact 1 is what gives the instrument a POSITIVE control that
MOVES: at origin/main it returns 2 implementations, at the GW3 head it returns 1.
A scanner that reports the same thing at both refs has not been shown to read
anything.

WHY THIS IS NOT `git grep`. A grep for the deleted type name returns 0 at HEAD
whether the delete happened or the grep was pointed at the wrong tree. This parses
declarations and use sites, so it can distinguish "there is one implementation"
from "there are none" from "there are three", and it prints what it found.

Usage:
  offer-seam-scan.py --ref HEAD --json
  offer-seam-scan.py --ref HEAD --expect-implementations UpstreamPendingOffersStore
  offer-seam-scan.py --ref HEAD --expect-unconditional
  offer-seam-scan.py --ref HEAD --expect-no-concrete-uses
  offer-seam-scan.py --ref origin/main --expect-conditional     # POS control

Exit 0 when every requested expectation holds, 1 otherwise.
"""
import argparse
import json
import re
import subprocess
import sys

IFACE = "IPendingOffersStore"

# `class X : ... IPendingOffersStore ...` — C# puts the base list after a colon.
DECL = re.compile(
    r"\b(?:public|internal|private|sealed|abstract|partial|static|\s)*class\s+"
    r"(?P<name>\w+)\s*(?::\s*(?P<bases>[^{]+))?",
)


def sh(*args):
    p = subprocess.run(args, capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def files_at(ref, prefix):
    rc, out, err = sh("git", "ls-tree", "-r", "--name-only", ref, "--", prefix)
    if rc != 0:
        print(f"git ls-tree failed for {ref}:{prefix}: {err.strip()}", file=sys.stderr)
        sys.exit(2)
    # A silent empty listing is the CB2 trap (a wrong path returns 0 lines and
    # exit 0, which reads as "verified clean"). Refuse it.
    names = [l for l in out.split("\n") if l.endswith(".cs")]
    if not names:
        print(f"REFUSING: {ref}:{prefix} listed 0 .cs files — a wrong pathspec looks "
              f"identical to a clean result.", file=sys.stderr)
        sys.exit(2)
    return names


def blob(ref, path):
    rc, out, _ = sh("git", "show", f"{ref}:{path}")
    return out if rc == 0 else ""


def strip_comments_and_strings(text):
    """Return `text` with // and /* */ comments and "string literals" blanked out
    (length preserved so line numbers survive). A claim inside a comment is not a
    use site, and a type name inside a string literal is not an injection."""
    out = list(text)
    i, n = 0, len(text)
    mode = None  # None | 'line' | 'block' | 'str' | 'verbstr'
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if mode is None:
            if c == "/" and nxt == "/":
                mode = "line"
                out[i] = out[i + 1] = " "
                i += 2
                continue
            if c == "/" and nxt == "*":
                mode = "block"
                out[i] = out[i + 1] = " "
                i += 2
                continue
            if c == "@" and nxt == '"':
                mode = "verbstr"
                i += 2
                continue
            if c == '"':
                mode = "str"
                i += 1
                continue
            i += 1
            continue
        if mode == "line":
            if c == "\n":
                mode = None
            else:
                out[i] = " "
            i += 1
            continue
        if mode == "block":
            if c == "*" and nxt == "/":
                out[i] = out[i + 1] = " "
                mode = None
                i += 2
                continue
            if c != "\n":
                out[i] = " "
            i += 1
            continue
        if mode == "str":
            if c == "\\":
                out[i] = " "
                if i + 1 < n and text[i + 1] != "\n":
                    out[i + 1] = " "
                i += 2
                continue
            if c == '"' or c == "\n":
                mode = None
                i += 1
                continue
            out[i] = " "
            i += 1
            continue
        if mode == "verbstr":
            if c == '"' and nxt == '"':
                out[i] = out[i + 1] = " "
                i += 2
                continue
            if c == '"':
                mode = None
                i += 1
                continue
            if c != "\n":
                out[i] = " "
            i += 1
            continue
    return "".join(out)


def find_implementations(ref):
    impls = {}
    for path in files_at(ref, "src/"):
        text = blob(ref, path)
        if IFACE not in text:
            continue
        code = strip_comments_and_strings(text)
        for m in DECL.finditer(code):
            bases = (m.group("bases") or "")
            if re.search(rf"\b{IFACE}\b", bases):
                impls[m.group("name")] = path
    return impls


def registration_state(ref, impls):
    """Locate the AddSingleton<IPendingOffersStore>(...) registration in Program.cs
    and decide whether it is unconditional."""
    path = "src/JeebGateway/Program.cs"
    text = blob(ref, path)
    if not text:
        return {"found": False, "state": "PROGRAM_CS_MISSING", "evidence": []}
    code = strip_comments_and_strings(text)
    anchor = re.search(rf"AddSingleton<{IFACE}>\s*\(", code)
    if not anchor:
        return {"found": False, "state": "NO_REGISTRATION", "evidence": []}

    # Walk the balanced parens of the registration call.
    start = anchor.end() - 1
    depth, j = 0, start
    while j < len(code):
        if code[j] == "(":
            depth += 1
        elif code[j] == ")":
            depth -= 1
            if depth == 0:
                break
        j += 1
    expr = code[start:j + 1]
    line0 = code[:anchor.start()].count("\n") + 1
    line1 = code[:j].count("\n") + 1

    branchy = []
    # A flag read inside the registration expression is the conditional shape the
    # false comment described.
    if re.search(r"UpstreamFeatureFlags|flags\s*\.\s*Offer|\bif\s*\(", expr):
        branchy.append("flag-read-or-if inside the registration expression")
    # …as is resolving a SECOND concrete store from the container.
    for name in impls:
        if re.search(rf"GetRequiredService<\s*{name}\s*>", expr):
            branchy.append(f"GetRequiredService<{name}> inside the registration expression")

    return {
        "found": True,
        "lines": [line0, line1],
        "state": "CONDITIONAL" if branchy else "UNCONDITIONAL",
        "evidence": branchy,
        "expr_oneline": re.sub(r"\s+", " ", expr).strip()[:400],
    }


def concrete_uses(ref, impls, reg):
    """Concrete store type names appearing in src/ CODE (not comments, not string
    literals) outside their own declaring file and outside the registration."""
    hits = []
    reg_lo, reg_hi = (reg.get("lines") or [0, 0])
    for path in files_at(ref, "src/"):
        text = blob(ref, path)
        if not any(name in text for name in impls):
            continue
        code = strip_comments_and_strings(text)
        for name, decl_path in impls.items():
            if path == decl_path:
                continue  # its own file: the declaration and its own ctor
            for m in re.finditer(rf"\b{name}\b", code):
                line = code[:m.start()].count("\n") + 1
                if path == "src/JeebGateway/Program.cs" and reg_lo <= line <= reg_hi:
                    continue  # THE registration — explicitly exempt, per V-2's wording
                snippet = text.split("\n")[line - 1].strip()[:160]
                hits.append({"type": name, "file": path, "line": line, "text": snippet})
    return hits


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", required=True)
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--expect-implementations", nargs="*", default=None,
                    help="exact set of concrete IPendingOffersStore classes under src/")
    ap.add_argument("--expect-unconditional", action="store_true")
    ap.add_argument("--expect-conditional", action="store_true")
    ap.add_argument("--expect-no-concrete-uses", action="store_true")
    a = ap.parse_args()

    impls = find_implementations(a.ref)
    reg = registration_state(a.ref, impls)
    uses = concrete_uses(a.ref, impls, reg)

    report = {
        "ref": a.ref,
        "sha": sh("git", "rev-parse", a.ref)[1].strip(),
        "implementations": {k: v for k, v in sorted(impls.items())},
        "registration": reg,
        "concrete_uses_outside_declaration_and_registration": uses,
    }
    if a.json:
        print(json.dumps(report, indent=2))
    else:
        print(f"ref                 : {a.ref} = {report['sha']}")
        print(f"implementations     : {', '.join(sorted(impls)) or '<none>'}")
        for k, v in sorted(impls.items()):
            print(f"                      {k}  <- {v}")
        print(f"registration        : {reg['state']}  (Program.cs lines {reg.get('lines')})")
        for e in reg["evidence"]:
            print(f"                      ! {e}")
        print(f"                      {reg.get('expr_oneline','')}")
        print(f"concrete uses       : {len(uses)}")
        for h in uses:
            print(f"                      {h['file']}:{h['line']}  {h['type']}  | {h['text']}")

    rc = 0
    if a.expect_implementations is not None:
        want = sorted(a.expect_implementations)
        got = sorted(impls)
        if want != got:
            print(f"EXPECT-FAIL implementations: wanted {want}, got {got}", file=sys.stderr)
            rc = 1
    if a.expect_unconditional and reg["state"] != "UNCONDITIONAL":
        print(f"EXPECT-FAIL registration: wanted UNCONDITIONAL, got {reg['state']} "
              f"({'; '.join(reg['evidence'])})", file=sys.stderr)
        rc = 1
    if a.expect_conditional and reg["state"] != "CONDITIONAL":
        print(f"EXPECT-FAIL registration: wanted CONDITIONAL, got {reg['state']}", file=sys.stderr)
        rc = 1
    if a.expect_no_concrete_uses and uses:
        print(f"EXPECT-FAIL concrete uses: wanted 0, got {len(uses)}", file=sys.stderr)
        rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
