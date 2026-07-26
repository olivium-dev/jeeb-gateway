#!/usr/bin/env bash
#
# .50 BAN GATE — owner directive, 2026-07-26 (jeeb-workspace/AGENTS.md):
#
#   "There must NEVER be any call to, or any communication with, the .50 server."
#
# 192.168.2.50 is the legacy Jeeb swarm host. It is UNROUTABLE from the only live
# gateway host (MSI 192.168.2.39): from that box both 192.168.2.50:10026 and
# 192.168.2.50:10040 answer 000. Live traffic survives today only because MSI's
# runtime env file overrides the committed defaults with 127.0.0.1. That makes
# every committed .50 default a landmine: a deploy onto a host without those
# overrides, or an env-file regression, silently repoints the gateway at a dead
# address and it presents as a product bug, not a config bug.
#
# This gate fails the build on any NON-COMMENT occurrence of 192.168.2.50 in a
# tracked file. Comments are allowed on purpose: they document the historical
# swarm topology and cannot dial anything.
#
# Exceptions live in scripts/no-50-allowlist.txt as exact file+line pairs. The
# allowlist is SELF-LIQUIDATING: a stale entry (one that no longer matches) also
# fails the build, so an exception cannot outlive the reason it was granted.
#
# There is also an inline opt-out for the narrow case of code whose PURPOSE is to
# detect the pattern (a guard step must name what it forbids). Put
#
#     no-50-gate:allow <reason>
#
# on the line. A reason is mandatory and every use is printed in the gate output,
# so a pragma cannot hide. Use the allowlist, not the pragma, for a real
# .50 destination that is merely waiting on a decision.
#
# Usage:  bash scripts/check-no-50-server.sh
# Exit:   0 = clean, 1 = violation (or stale allowlist entry)

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

SELF="scripts/check-no-50-server.sh"
ALLOWLIST="scripts/no-50-allowlist.txt"

# The gate and its allowlist necessarily contain the pattern; exclude them.
# -I skips binary files. Fixed-string match, so no regex escaping worries.
HITS="$(git grep -n -I -F '192.168.2.50' -- \
          ":(exclude)${SELF}" ":(exclude)${ALLOWLIST}" || true)"

ALLOW_CONTENT=""
if [ -f "$ALLOWLIST" ]; then
  ALLOW_CONTENT="$(cat "$ALLOWLIST")"
fi

printf '%s' "$HITS" | ALLOWLIST_TEXT="$ALLOW_CONTENT" ALLOWLIST_PATH="$ALLOWLIST" python3 -c '
import os, sys

PATTERN = "192.168.2.50"

WHOLE_LINE_COMMENT_STARTS = ("//", "#", "<!--", "/*", "*", "--", ";")
HASH_COMMENT_EXTS = (".yml", ".yaml", ".py", ".sh", ".bash", ".toml", ".env",
                     ".cfg", ".ini", ".conf", ".tf", ".rb", ".pl", ".r")


def in_comment(path, line, idx):
    """True when the PATTERN occurrence at column idx sits inside a comment.

    Deliberately scheme-aware: the "//" in "http://192.168.2.50" is NOT a comment
    marker. Splitting the line on the first "//" -- the obvious implementation --
    would silently pass every "http://192.168.2.50" literal in the repo, i.e. it
    would pass exactly the lines this gate exists to catch.
    """
    stripped = line.lstrip()
    if stripped.startswith(WHOLE_LINE_COMMENT_STARTS):
        return True
    # JSON has no comment syntax; this repo documents config with a "_comment" key.
    if stripped.startswith(("\"_comment\"", "\x27_comment\x27")):
        return True

    prefix = line[:idx]

    # A "//" earlier on the line opens a comment only when it is not part of "://".
    i = 0
    while True:
        j = prefix.find("//", i)
        if j < 0:
            break
        if j == 0 or prefix[j - 1] != ":":
            return True
        i = j + 2

    if "<!--" in prefix or "/*" in prefix:
        return True

    ext = os.path.splitext(path)[1].lower()
    if ext in HASH_COMMENT_EXTS and "#" in prefix:
        return True

    return False


allow_path = os.environ["ALLOWLIST_PATH"]
allow = {}
for raw in os.environ.get("ALLOWLIST_TEXT", "").splitlines():
    entry = raw.strip()
    if not entry or entry.startswith("#"):
        continue
    if ":" not in entry:
        print("::error::%s: malformed entry (want <path>:<exact trimmed line>): %s"
              % (allow_path, entry))
        sys.exit(1)
    p, content = entry.split(":", 1)
    allow.setdefault((p.strip(), content.strip()), 0)

PRAGMA = "no-50-gate:allow"

violations = []
comments = 0
pragmas = []
bad_pragmas = []

for raw in sys.stdin.read().splitlines():
    if not raw.strip():
        continue
    # git grep -n output: <path>:<lineno>:<content>
    try:
        path, lineno, content = raw.split(":", 2)
    except ValueError:
        continue
    idx = content.find(PATTERN)
    if idx < 0:
        continue
    if PRAGMA in content:
        reason = content.split(PRAGMA, 1)[1].strip().lstrip("-#*/ ").strip()
        if not reason:
            bad_pragmas.append((path, lineno, content.strip()))
        else:
            pragmas.append((path, lineno, reason))
        continue
    if in_comment(path, content, idx):
        comments += 1
        continue
    key = (path, content.strip())
    if key in allow:
        allow[key] += 1
        continue
    violations.append((path, lineno, content.strip()))

stale = [k for k, n in allow.items() if n == 0]

if violations:
    print("::error::.50 BAN VIOLATION -- %d non-comment reference(s) to %s in tracked files."
          % (len(violations), PATTERN))
    print("")
    print("Owner directive 2026-07-26: there must NEVER be any call to, or any")
    print("communication with, the .50 server. 192.168.2.50 is unroutable from the")
    print("live gateway host, so any code path that reaches it is dead on arrival.")
    print("")
    for path, lineno, content in violations:
        print("  %s:%s" % (path, lineno))
        print("      %s" % content[:200])
    print("")
    print("Fix: point the value at the host the runtime env file actually exports")
    print("(MSI gateway.env uses 127.0.0.1 for every service leg). Do NOT invent a")
    print("host. If an occurrence is genuinely unavoidable, add an exact entry to")
    print("%s with a dated reason." % allow_path)

if stale:
    print("::error::%s has %d STALE entry/entries -- they no longer match any tracked line."
          % (allow_path, len(stale)))
    print("An exception must not outlive its reason. Delete these lines:")
    for p, content in stale:
        print("  %s:%s" % (p, content[:200]))

if bad_pragmas:
    print("::error::%d %s pragma(s) with no reason. A reason is mandatory."
          % (len(bad_pragmas), PRAGMA))
    for path, lineno, content in bad_pragmas:
        print("  %s:%s" % (path, lineno))
        print("      %s" % content[:200])

if violations or stale or bad_pragmas:
    sys.exit(1)

print("OK: no non-comment reference to %s in tracked files." % PATTERN)
print("    %d comment reference(s) allowed (historical swarm topology, cannot dial)." % comments)
if pragmas:
    print("    %d inline %s pragma(s) — every use is listed so none can hide:"
          % (len(pragmas), PRAGMA))
    for path, lineno, reason in pragmas:
        print("      %s:%s  %s" % (path, lineno, reason[:120]))
if allow:
    print("    %d allowlisted exception(s), all still matching:" % len(allow))
    for (p, content), n in allow.items():
        print("      %s  (x%d)" % (p, n))
'
