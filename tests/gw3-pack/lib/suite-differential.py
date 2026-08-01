#!/usr/bin/env python3
"""
GW3 — the churned-fixture instrument: does this branch introduce a NEW test failure?

GW3's W3.5(c) re-points 23 fixture references across 10 test files and adds an
explicit store registration to 5 more. The obvious assertion — "those files are all
green" — is a FALSE RED on this machine, and the pack must not carry it:

    origin/main, same filter, same runner:  4 of 141 already fail
      PostgresRequestExpiryAuthorityTests x2   Docker is not running (Testcontainers)
      ClientVisibilityAndReceiptTests.GetById_AfterFullLifecycleToDone_...
      OfferPushNotifierTests.AC3_...           order-dependent; passes when run alone

A pack that reds on those blames GW3 for the state of the machine. So the predicate
is a DIFFERENTIAL: the set of tests failing at HEAD must be a subset of the set
failing at BASE. Zero NEW failures. That can still go red — and does — while being
immune to Docker and to pre-existing flake.

TWO ANTI-FLAKE RULES, because a single sample is not a verdict here
(OWNER-DECISIONS.md 2026-07-31 10:32Z, mitigation 2):

  1. Both sides run the SAME filter through the SAME runner, so an order-dependent
     failure appears in both sets and cancels out.
  2. Every candidate NEW failure is then RE-RUN INDIVIDUALLY at both refs before it
     is reported. A test that passes alone at HEAD is demoted to ORDER-DEPENDENT and
     reported as such, not as a regression.

The BASE side is a `git archive` export built once and cached, never the live tree —
the live tree is the branch under test.

Usage:
  suite-differential.py --filter "<xunit filter>" [--base origin/main]
                        [--base-dir <cache>] [--json]
Exit 0 when there are no NEW failures.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

PROJ = "tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj"
FAILED = re.compile(r"^\s*Failed\s+(\S+)\s+\[", re.M)
SUMMARY = re.compile(r"^(?:Passed!|Failed!).*$", re.M)


def run(cmd, cwd=None):
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)


def repo_root():
    return run(["git", "rev-parse", "--show-toplevel"]).stdout.strip()


def ensure_base_export(root, base, base_dir):
    """git archive <base> into base_dir and build it. Cached: an existing build with
    a matching .sha stamp is reused."""
    sha = run(["git", "rev-parse", base], cwd=root).stdout.strip()
    stamp = os.path.join(base_dir, ".gw3-base-sha")
    if os.path.isfile(stamp) and open(stamp).read().strip() == sha:
        return sha, "cached"

    if os.path.isdir(base_dir):
        shutil.rmtree(base_dir)
    os.makedirs(base_dir, exist_ok=True)
    tar_path = os.path.join(tempfile.gettempdir(), f"gw3-base-{sha[:8]}.tar")
    with open(tar_path, "wb") as fh:
        p = subprocess.run(["git", "archive", base], cwd=root, stdout=fh)
    if p.returncode != 0:
        print(f"git archive {base} failed", file=sys.stderr)
        sys.exit(2)
    with tarfile.open(tar_path) as tf:
        tf.extractall(base_dir)
    os.unlink(tar_path)

    b = run(["dotnet", "build", PROJ, "-v", "q", "--nologo"], cwd=base_dir)
    if b.returncode != 0:
        print(f"BASE export failed to build — the differential is unmeasurable.\n"
              f"{b.stdout[-2000:]}", file=sys.stderr)
        sys.exit(2)
    with open(stamp, "w") as fh:
        fh.write(sha)
    return sha, "built"


def test_run(cwd, flt):
    p = run(["dotnet", "test", PROJ, "--no-build", "--nologo", "--filter", flt], cwd=cwd)
    out = p.stdout + p.stderr
    failed = set(FAILED.findall(out))
    summary = (SUMMARY.search(out) or type("m", (), {"group": lambda s: "<no summary>"})()).group()
    # A filter that matched nothing is not a green run.
    total = re.search(r"Total:\s+(\d+)", summary or "")
    return failed, (summary or "").strip(), int(total.group(1)) if total else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--filter", required=True)
    ap.add_argument("--base", default="origin/main")
    ap.add_argument("--base-dir", default=os.environ.get(
        "GW3_BASE_EXPORT", os.path.join(tempfile.gettempdir(), "gw3-base-export")))
    ap.add_argument("--min-tests", type=int, default=20,
                    help="refuse a differential drawn from fewer tests than this")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    root = repo_root()
    head_sha = run(["git", "rev-parse", "HEAD"], cwd=root).stdout.strip()
    base_sha, how = ensure_base_export(root, a.base, a.base_dir)

    head_failed, head_sum, head_total = test_run(root, a.filter)
    base_failed, base_sum, base_total = test_run(a.base_dir, a.filter)

    print(f"HEAD  {head_sha}   {head_sum}")
    print(f"BASE  {base_sha} ({a.base}, export {how} at {a.base_dir})")
    print(f"      {base_sum}")

    rc = 0
    if head_total < a.min_tests or base_total < a.min_tests:
        print(f"REFUSING: {head_total}/{base_total} tests matched (< {a.min_tests}). "
              f"A differential drawn from an empty filter is vacuously clean.", file=sys.stderr)
        return 1

    candidates = sorted(head_failed - base_failed)
    fixed = sorted(base_failed - head_failed)
    shared = sorted(head_failed & base_failed)

    print(f"pre-existing failures (red at BOTH refs, NOT this branch's): {len(shared)}")
    for t in shared:
        print(f"      = {t}")
    if fixed:
        print(f"failures this branch REMOVED: {len(fixed)}")
        for t in fixed:
            print(f"      - {t}")

    # Re-run each candidate ALONE at both refs. A single batch sample is not a verdict.
    regressions, order_dependent = [], []
    for t in candidates:
        one = f"FullyQualifiedName~{t}"
        h_fail, h_sum, h_tot = test_run(root, one)
        b_fail, b_sum, b_tot = test_run(a.base_dir, one)
        solo_head_red = h_tot > 0 and bool(h_fail)
        solo_base_red = b_tot > 0 and bool(b_fail)
        row = {"test": t, "solo_head_red": solo_head_red, "solo_base_red": solo_base_red,
               "solo_head": h_sum, "solo_base": b_sum}
        if solo_head_red and not solo_base_red:
            regressions.append(row)
        else:
            order_dependent.append(row)

    print(f"NEW failures at HEAD (batch run): {len(candidates)}")
    for r in regressions:
        print(f"      ! REGRESSION  {r['test']}")
        print(f"          solo @HEAD: {r['solo_head']}")
        print(f"          solo @BASE: {r['solo_base']}")
    for r in order_dependent:
        print(f"      ? ORDER-DEPENDENT (not attributed to this branch)  {r['test']}")
        print(f"          solo @HEAD: {r['solo_head']}")
        print(f"          solo @BASE: {r['solo_base']}")

    if regressions:
        rc = 1
        print(f"VERDICT: {len(regressions)} test(s) pass at {a.base} and fail at HEAD, "
              f"confirmed by an individual re-run at both refs.")
    else:
        print("VERDICT: no test that passes at the base fails on this branch.")

    if a.json:
        print(json.dumps({"head": head_sha, "base": base_sha,
                          "pre_existing": shared, "removed": fixed,
                          "regressions": regressions,
                          "order_dependent": order_dependent}, indent=2))
    return rc


if __name__ == "__main__":
    sys.exit(main())
