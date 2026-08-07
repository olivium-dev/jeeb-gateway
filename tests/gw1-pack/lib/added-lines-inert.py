#!/usr/bin/env python3
"""
GW1 test pack, HYGIENE leg — every added line carrying a prohibited private-host
token must be inert. The classifier treats a line as live when it carries a
scheme, host:port, BaseUrl, or HttpClient binding.

`--allow <path-substring>` exists for exactly one reason and it is the reason
`scripts/no-50-allowlist.txt` exists in this repo: **a gate must name what it
forbids**. `tests/gw1-pack/neg-controls.sh` contains a LIVE-looking
`http://192.168.2.50:10040` because control N21 injects one to prove H1 can go
red. Without the allowlist H1 reds on its own negative control. Allowed hits are
never hidden — each is printed as [ALLOWED] with its path, and the count is in the
summary, so the allowlist can be audited rather than trusted.

Also note, and this is why the check is a script rather than a shell one-liner:
`git diff … | grep -c` returned **0** for a matching line when run
through this machine's rtk git proxy, and **1** when git was invoked directly. A
shell pipeline here is not a reliable instrument.

Usage:  added-lines-inert.py <base-ref> <token> [--allow <substr>]... [-- pathspec ...]
Exit:   0 = no LIVE added reference; 1 = at least one, or a control failed.
"""
from __future__ import annotations

import re
import subprocess
import sys

DIALS = [
    re.compile(r"https?://", re.I),
    re.compile(r"\b\d{1,3}(\.\d{1,3}){3}\s*:\s*\d+"),   # host:port
    re.compile(r"BaseUrl", re.I),
    re.compile(r"AddHttpClient|new\s+HttpClient|HttpClient\s+\w+\s*[,)]"),
]
# Comment openers for the file types this repo actually adds: C#, SQL, shell, JSON tombstones.
COMMENT = re.compile(r'^\s*(//|///|--|#|\*|/\*|"_comment)')


def main() -> int:
    argv = sys.argv[1:]
    if len(argv) < 2:
        print(__doc__)
        return 2
    base, token = argv[0], argv[1]
    rest, allows, pathspec = argv[2:], [], []
    i = 0
    while i < len(rest):
        if rest[i] == "--allow":
            allows.append(rest[i + 1]); i += 2
        elif rest[i] == "--":
            pathspec = rest[i + 1:]; break
        else:
            pathspec.append(rest[i]); i += 1

    cmd = ["git", "diff", f"{base}..HEAD"]
    if pathspec:
        cmd += ["--"] + pathspec
    diff = subprocess.run(cmd, capture_output=True, text=True, check=False).stdout

    print(f"token            : {token}")
    print(f"pathspec         : {pathspec or ['<all>']}")
    print(f"allowlist        : {allows or ['<none>']}")

    cur = "<unknown>"
    live, allowed, inert = [], [], []
    total_added = 0
    for line in diff.splitlines():
        if line.startswith("+++ b/"):
            cur = line[6:]; continue
        if line.startswith("+++") or not line.startswith("+"):
            continue
        total_added += 1
        if token not in line:
            continue
        body = line[1:]
        if any(a in cur for a in allows):
            allowed.append((cur, body)); continue
        is_comment = bool(COMMENT.match(body))
        dialing = [p.pattern for p in DIALS if p.search(body)]
        # LIVE iff it dials. `is_comment` is reported for the reader, never used
        # to convict: a bare mention in a string literal or an assertion is inert.
        (live if dialing else inert).append((cur, body, is_comment, dialing))

    for path, body in allowed:
        print(f"  [ALLOWED] {path} :: {body.strip()[:110]}")
    for path, body, is_comment, dialing in inert:
        print(f"  [INERT]   {path} comment={is_comment} :: {body.strip()[:110]}")
    for path, body, is_comment, dialing in live:
        print(f"  [LIVE]    {path} comment={is_comment} dials={dialing} :: {body.strip()[:110]}")

    print(f"added lines with the token: live={len(live)} inert={len(inert)} allowed={len(allowed)}")

    failed = False
    # POSITIVE CONTROL 1 — the diff reader must be able to see added lines at all.
    print(f"POS CONTROL: total added lines visible to this reader = {total_added}")
    if total_added == 0:
        print("FAIL positive control: the diff reader saw no added lines, so a zero token count "
              "proves nothing")
        failed = True
    # POSITIVE CONTROL 2 — the allowlist is reported, never silent, so it can be
    # audited rather than trusted.
    if allows:
        print(f"POS CONTROL: the allowlist suppressed {len(allowed)} line(s); "
              f"{len(inert) + len(live)} token line(s) were still classified")

    if live:
        print(f"FAIL: {len(live)} LIVE added reference(s) to '{token}'")
        failed = True
    elif not failed:
        print(f"OK: no LIVE added reference to '{token}'")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
