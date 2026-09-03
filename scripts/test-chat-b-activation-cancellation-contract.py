#!/usr/bin/env python3
"""Executable negative controls for Chat B cancellation recovery structure."""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/jeeb-chat-b-activation.yml"
VALIDATOR = ROOT / "scripts/validate-chat-b-activation-authority.py"
CHECKOUT_SHA = "11d5960a326750d5838078e36cf38b85af677262"
JOB_ALWAYS = "  activation:\n    if: ${{ always() }}"
RECOVERY_ALWAYS = (
    "        if: ${{ always() && steps.arm.outputs.armed == 'true' "
    "&& steps.live_smoke.outcome != 'success' }}"
)
ARM_BLOCK = """
      - name: Arm cancellation-safe Chat B mutation
        id: arm
        run: |
          set -euo pipefail
          printf 'armed=true\\n' >> "$GITHUB_OUTPUT"
"""


def validate(source: str) -> subprocess.CompletedProcess[str]:
    with tempfile.TemporaryDirectory(prefix="jeeb-chat-b-contract-") as directory:
        candidate = Path(directory) / WORKFLOW.name
        candidate.write_text(source, encoding="utf-8")
        environment = os.environ.copy()
        environment["JEEB_CHAT_B_WORKFLOW_UNDER_TEST"] = str(candidate)
        environment["PYTHONDONTWRITEBYTECODE"] = "1"
        return subprocess.run(
            ["python3", str(VALIDATOR)],
            cwd=ROOT,
            env=environment,
            text=True,
            capture_output=True,
            check=False,
        )


def replace_once(source: str, old: str, new: str, label: str) -> str:
    if source.count(old) != 1:
        raise SystemExit(f"FAIL: {label} fixture marker count drifted")
    return source.replace(old, new, 1)


source = WORKFLOW.read_text(encoding="utf-8")
baseline = validate(source)
if baseline.returncode != 0:
    raise SystemExit(f"FAIL: valid cancellation contract was rejected: {baseline.stderr}")

arm_after_mutation = replace_once(source, ARM_BLOCK, "", "arm block")
arm_after_mutation = replace_once(
    arm_after_mutation,
    "      - name: Prove live Jeeb to Firebase identity exchange\n",
    f"{ARM_BLOCK}\n      - name: Prove live Jeeb to Firebase identity exchange\n",
    "live smoke step",
)

negative_cases = (
    (
        "job not cancellation-persistent",
        replace_once(
            source,
            JOB_ALWAYS,
            "  activation:\n    if: ${{ success() }}",
            "job always",
        ),
        "must remain running during cancellation",
    ),
    (
        "recovery is failure-only",
        replace_once(
            source,
            RECOVERY_ALWAYS,
            "        if: ${{ failure() && steps.arm.outputs.armed == 'true' }}",
            "recovery always",
        ),
        "recovery is not armed and cancellation-safe",
    ),
    (
        "mutation armed after remote update",
        arm_after_mutation,
        "arm/activation/smoke/recovery/assertion order drifted",
    ),
    (
        "floating checkout",
        replace_once(
            source,
            f"actions/checkout@{CHECKOUT_SHA}",
            "actions/checkout@v4",
            "checkout pin",
        ),
        "not pinned to a full 40-character SHA",
    ),
    (
        "final assertion not unconditional",
        replace_once(
            source,
            "      - name: Require completed activation and live identity proof\n"
            "        if: ${{ always() }}",
            "      - name: Require completed activation and live identity proof\n"
            "        if: ${{ success() }}",
            "final assertion",
        ),
        "final required assertion does not run after cancellation or failure",
    ),
)

for label, candidate, expected_error in negative_cases:
    result = validate(candidate)
    output = f"{result.stdout}\n{result.stderr}"
    if result.returncode == 0:
        raise SystemExit(f"FAIL: negative cancellation contract passed: {label}")
    if expected_error not in output:
        raise SystemExit(
            f"FAIL: negative cancellation contract was not discriminating: {label}: {output}"
        )

print("Jeeb Chat B cancellation and immutable-checkout negative controls: PASS")
