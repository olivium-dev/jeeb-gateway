#!/usr/bin/env bash
# Every shell variable expanded inside the QUOTED remote heredoc must be forwarded to the
# staging host. The heredoc is quoted, so the runner does not expand it and the remote runs
# `set -euo pipefail`: an unforwarded name is an unbound-variable abort mid-deploy.
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

python3 - "${JEEB_STAGING_WORKFLOW_UNDER_TEST:-.github/workflows/jeeb-staging-deploy.yml}" <<'PY'
import json
import re
import subprocess
import sys
from pathlib import Path

WORKFLOW = Path(sys.argv[1])
STEP = "Deploy Swarm service"

# Names the remote shell always provides; never forwarded.
AMBIENT = {
    "HOME", "PATH", "PWD", "OLDPWD", "IFS", "USER", "LOGNAME", "SHELL", "HOSTNAME",
    "TMPDIR", "TERM", "LANG", "UID", "EUID", "PPID", "RANDOM", "LINENO", "SECONDS",
    "REPLY", "FUNCNAME", "PIPESTATUS", "BASH_REMATCH", "BASH_SOURCE", "BASHPID",
    "BASH_VERSION", "SHLVL", "COLUMNS", "LINES",
}


def fail(message):
    raise SystemExit(f"FAIL: {message}")


def load_step_body():
    ruby = (
        'require "json"; require "yaml"; '
        'd = YAML.safe_load(File.read(ARGV.fetch(0)), aliases: true); '
        'step = d["jobs"]["deploy"]["steps"].find { |s| s["name"] == ARGV.fetch(1) }; '
        'raise "step not found" unless step; '
        'STDOUT.write(JSON.generate(step["run"]))'
    )
    try:
        out = subprocess.check_output(
            ["ruby", "-rjson", "-ryaml", "-e", ruby, str(WORKFLOW), STEP], text=True)
    except (OSError, subprocess.CalledProcessError) as error:
        fail(f"cannot parse {WORKFLOW.name}: {error}")
    return json.loads(out)


def strip_literals(text):
    """Blank out single-quoted spans and comments: bash performs no expansion there, and jq
    programs (single-quoted) are full of $name tokens that are jq variables, not shell. A
    $( ) substitution opens a fresh quoting context, so quote state is stacked across it."""
    out = []
    in_single = in_double = in_comment = False
    stack = []
    parens = 0
    previous = "\n"
    index = 0
    while index < len(text):
        char = text[index]
        if in_comment:
            out.append(char if char == "\n" else " ")
            if char == "\n":
                in_comment = False
            previous = char
            index += 1
            continue
        if not in_single and char == "\\" and index + 1 < len(text):
            # A backslash escape never opens or closes a quote span.
            out.append(char)
            out.append(text[index + 1])
            previous = text[index + 1]
            index += 2
            continue
        if in_single:
            out.append(" " if char != "\n" else char)
            if char == "'":
                in_single = False
            previous = char
            index += 1
            continue
        if char == "$" and index + 1 < len(text) and text[index + 1] == "(":
            stack.append((in_single, in_double, parens))
            in_single = in_double = False
            parens = 0
            out.append("$(")
            previous = "("
            index += 2
            continue
        if char == ")" and not in_double:
            if parens:
                parens -= 1
            elif stack:
                in_single, in_double, parens = stack.pop()
            out.append(char)
            previous = char
            index += 1
            continue
        if char == "(" and not in_double:
            parens += 1
            out.append(char)
            previous = char
            index += 1
            continue
        if char == "'" and not in_double:
            in_single = True
            out.append(" ")
            previous = char
            index += 1
            continue
        if char == '"':
            in_double = not in_double
            out.append(char)
            previous = char
            index += 1
            continue
        if char == "#" and not in_double and previous in " \t\n;&|(":
            in_comment = True
            out.append(" ")
            previous = char
            index += 1
            continue
        out.append(char)
        previous = char
        index += 1
    return "".join(out)


REFERENCE = re.compile(r"\$\{?#?([A-Za-z_][A-Za-z0-9_]*)")
ASSIGNMENTS = (
    # Assignments at the start of a line OR after ; && || ( — `a=1; b=2` is one line.
    re.compile(r"(?:(?m:^)|[;&|(){}]|\bdo\b|\bthen\b)[ \t]*"
               r"(?:local|export|declare|typeset|readonly)?[ \t-]*"
               r"([A-Za-z_][A-Za-z0-9_]*)\+?="),
    re.compile(r"(?m)\b(?:local|export|declare|typeset|readonly)[ \t]+(?:-[A-Za-z]+[ \t]+)?"
               r"((?:[A-Za-z_][A-Za-z0-9_]*[ \t]*)+)"),
    re.compile(r"(?m)^[ \t]*for[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]+in\b"),
    re.compile(r"(?m)\bread[ \t]+(?:-[A-Za-z]+[ \t]+)*((?:[A-Za-z_][A-Za-z0-9_]*[ \t]*)+)"),
    re.compile(r"(?m)\b(?:mapfile|readarray)[ \t]+(?:-[A-Za-z]+[ \t]*\S*[ \t]+)*"
               r"([A-Za-z_][A-Za-z0-9_]*)"),
    re.compile(r"(?m)\bprintf[ \t]+-v[ \t]+([A-Za-z_][A-Za-z0-9_]*)"),
)


def assigned_names(text):
    names = set()
    for pattern in ASSIGNMENTS:
        for match in pattern.findall(text):
            names.update(match.split())
    return names


def analyse(body, label):
    """Returns (referenced-but-unassigned names, forwarded names) for one remote heredoc."""
    start = body.index("cat <<'REMOTE'\n")
    end = body.index("\nREMOTE\n", start)
    heredoc = body[start + len("cat <<'REMOTE'\n"):end]

    serializer = re.search(
        r"for variable in\s+(.*?)\s*;\s*do\s*\n\s*printf '%s=%q\\n'", body, re.S)
    if serializer is None:
        fail(f"{label}: the forwarded-variable serializer is missing or reshaped")
    forwarded = set(serializer.group(1).replace("\\\n", " ").split())
    if not forwarded:
        fail(f"{label}: the forwarded-variable list is empty")

    scannable = strip_literals(heredoc)
    referenced = set(REFERENCE.findall(scannable))
    resolved = assigned_names(scannable) | AMBIENT | {str(n) for n in range(10)}
    return referenced - resolved, forwarded, heredoc


body = load_step_body()
unresolved, forwarded, heredoc = analyse(body, STEP)

missing = sorted(unresolved - forwarded)
if missing:
    fail(
        "the REMOTE heredoc expands variable(s) the deploy never forwards to the staging "
        f"host: {', '.join(missing)}. The heredoc is quoted and the remote runs "
        "'set -euo pipefail', so this aborts the deploy with 'unbound variable'. Add each "
        "name to the 'for variable in ...' list."
    )

# Negative control: the check must actually detect the class it exists for.
canary = heredoc + '\n          add_env Canary__Flag "$never_forwarded_canary"\n'
if "never_forwarded_canary" not in set(REFERENCE.findall(strip_literals(canary))):
    fail("the reference scanner no longer detects an unforwarded expansion")
# ...and must not flag a jq program variable, which is what makes it usable.
if "jq_only_canary" in set(REFERENCE.findall(strip_literals(
        "jq -n --arg jq_only_canary x '{a: $jq_only_canary}'\n"))):
    fail("the reference scanner mistakes a jq program variable for a shell expansion")

unused = sorted(forwarded - unresolved)
print(
    f"REMOTE heredoc forwarding is complete: {len(unresolved)} expanded name(s) all forwarded"
    + (f"; {len(unused)} forwarded but unused ({', '.join(unused)})" if unused else "")
)
PY
