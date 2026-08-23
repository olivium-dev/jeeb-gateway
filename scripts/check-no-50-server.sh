#!/usr/bin/env bash
#
# Owner directive: no code or configuration may communicate with the legacy .50 host.
# Historical comments may name it, but executable/configured occurrences fail.
# There is deliberately no allowlist: a deploy destination cannot be waived.

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

export BANNED_TARGET_HOST="192.168.2.$((25 * 2))"

# Scan every tracked and non-ignored untracked file. Even this gate and its
# negative controls construct the sentinel from parts, so no path exception is
# needed and a future executable occurrence cannot hide in test machinery.
HITS="$(git grep --untracked --exclude-standard -n -I -F "$BANNED_TARGET_HOST" || true)"

printf '%s' "$HITS" | python3 -c '
import os
import sys

PATTERN = os.environ["BANNED_TARGET_HOST"]
WHOLE_LINE_COMMENT_STARTS = ("//", "#", "*", "--", ";")


def in_comment(path: str, line: str, idx: int) -> bool:
    """Return true only for an unambiguous whole-line comment/tombstone."""
    del path
    stripped = line.lstrip()
    if stripped.startswith(WHOLE_LINE_COMMENT_STARTS):
        return True
    prefix = line[:idx].lstrip()
    if prefix.startswith("<!--") and "-->" not in prefix:
        return True
    if prefix.startswith("/*") and "*/" not in prefix:
        return True
    if stripped.startswith(("\"_comment\"", "\047_comment\047")):
        return True
    return False


# Guard the classifier itself: URL schemes must never be mistaken for C# comments.
canaries = (
    ("deploy.yml", f"base_url: http://{PATTERN}:10040", False),
    ("appsettings.json", f"\"BaseUrl\": \"http://{PATTERN}:10040\"", False),
    ("Program.cs", f"var uri = \"http://{PATTERN}:10040\";", False),
    ("Program.cs", f"/* old */ var uri = \"http://{PATTERN}:10040\";", False),
    ("deploy.yml", f"base_url: http://{PATTERN}:10040 # old", False),
    ("Program.cs", f"// historical http://{PATTERN}:10040", True),
    ("appsettings.json", f"\"_comment\": \"historical {PATTERN}\"", True),
)
for path, line, expected in canaries:
    actual = in_comment(path, line, line.index(PATTERN))
    if actual != expected:
        raise SystemExit(f"FAIL: .50 classifier self-test failed for {path}: {line}")

violations = []
comments = 0
for raw in sys.stdin.read().splitlines():
    if not raw.strip():
        continue
    try:
        path, line_number, content = raw.split(":", 2)
    except ValueError:
        continue
    index = content.find(PATTERN)
    if index < 0:
        continue
    if in_comment(path, content, index):
        comments += 1
    else:
        violations.append((path, line_number, content.strip()))

if violations:
    print(
        f"::error::.50 BAN VIOLATION -- {len(violations)} executable/configured "
        f"reference(s) to {PATTERN}."
    )
    for path, line_number, content in violations:
        print(f"  {path}:{line_number}")
        print(f"      {content[:200]}")
    print("No allowlist exists. Remove the destination or leave an ambiguous deploy input empty and fail closed.")
    raise SystemExit(1)

print(f"OK: no executable/configured reference to {PATTERN} in repository files.")
print(f"    {comments} inert historical comment reference(s) remain.")
'
