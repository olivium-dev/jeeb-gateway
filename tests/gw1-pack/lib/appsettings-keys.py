#!/usr/bin/env python3
"""
GW1 test pack, W0.6 leg — KEY-LEVEL check of the shipped appsettings files.

Why not `git grep -F UseFcmTransport`. The delete deliberately leaves a
`_comment_Fcm_REMOVED` tombstone naming the three keys it removed, so a substring
grep for `UseFcmTransport` / `FcmProjectId` / `FcmBearerToken` matches the
tombstone and reds on correct work. That is a false red of exactly the shape this
programme keeps shipping. This script parses the JSON and asks about KEYS, which
is the only thing configuration binding can ever read.

It also carries its own POSITIVE CONTROL: the `Push` section must still contain at
least one live key. Without that, a script that failed to locate the section at all
would report "no forbidden keys" and look like a pass.

Usage:  appsettings-keys.py [--json]
Exit:   0 = clean, 1 = a forbidden key is live (or the section could not be found).
"""
from __future__ import annotations

import glob
import json
import re
import sys

FORBIDDEN_EXACT = {"UseFcmTransport", "FcmProjectId", "FcmBearerToken"}
# Any *live* key under Push: that starts with Fcm, or that looks like a transport
# switch, is forbidden — a re-add under a slightly different name must red too.
FORBIDDEN_PATTERNS = [re.compile(r"^Fcm"), re.compile(r"^Use.*Transport$")]
COMMENT_PREFIX = "_comment"


def strip_jsonc(text: str) -> str:
    """appsettings files here are plain JSON, but tolerate // comments defensively."""
    return re.sub(r"^\s*//[^\n]*$", "", text, flags=re.M)


def walk(node, path, hits, live_keys):
    if isinstance(node, dict):
        for k, v in node.items():
            here = f"{path}:{k}" if path else k
            if k.startswith(COMMENT_PREFIX):
                continue  # a tombstone is documentation; binding never reads it
            if path == "Push":
                live_keys.append(k)
                if k in FORBIDDEN_EXACT or any(p.match(k) for p in FORBIDDEN_PATTERNS):
                    hits.append(here)
            walk(v, here, hits, live_keys)
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, f"{path}[{i}]", hits, live_keys)


def main() -> int:
    files = sorted(glob.glob("src/JeebGateway/appsettings*.json"))
    if not files:
        print("FAIL: no appsettings*.json found under src/JeebGateway/")
        return 1

    report, failed = {}, False
    for f in files:
        with open(f, "r", encoding="utf-8") as fh:
            doc = json.loads(strip_jsonc(fh.read()))
        hits, live = [], []
        walk(doc, "", hits, live)
        has_push = "Push" in doc
        report[f] = {"has_push_section": has_push,
                     "live_push_keys": live,
                     "forbidden_live_keys": hits}
        status = "clean" if not hits else "FORBIDDEN"
        print(f"{f:<48} Push={'yes' if has_push else 'no ':<3} "
              f"live keys={live} forbidden={hits}  [{status}]")
        if hits:
            failed = True

    # POSITIVE CONTROL — the file that owns the Push section must still have live
    # keys in it. A parser that silently found nothing would otherwise "pass".
    base = "src/JeebGateway/appsettings.json"
    live_base = report.get(base, {}).get("live_push_keys", [])
    if not live_base:
        print(f"FAIL (positive control): {base} has no live keys under 'Push' — "
              "the parser did not reach the section, so 'no forbidden keys' proves nothing")
        failed = True
    else:
        print(f"POS CONTROL ok: {base} Push section has {len(live_base)} live key(s): {live_base}")

    if "--json" in sys.argv:
        print(json.dumps(report, indent=2))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
