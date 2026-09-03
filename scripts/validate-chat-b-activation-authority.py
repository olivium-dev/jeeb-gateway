#!/usr/bin/env python3
"""Structural gate for the only reviewed Jeeb Chat B mutation authority."""

from __future__ import annotations

import json
import os
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = Path(
    os.environ.get(
        "JEEB_CHAT_B_WORKFLOW_UNDER_TEST",
        ROOT / ".github/workflows/jeeb-chat-b-activation.yml",
    )
).resolve()
WORKFLOW_NAME = WORKFLOW.name
LIVE_SMOKE_WORKFLOW = ROOT / ".github/workflows/jeeb-chat-firebase-live-smoke.yml"
CHECKOUT_SHA = "11d5960a326750d5838078e36cf38b85af677262"
GATE_NAME = "Require protected source and designated Chat B owner"
ACTIVATE_NAME = "Activate exact Chat B flag with start-first pause policy"
SMOKE_NAME = "Prove live Jeeb to Firebase identity exchange"
FORWARD_FIX_NAME = "Forward-fix Chat to A1 after any incomplete activation"
JOB_CONDITION = "${{ always() }}"
FORWARD_FIX_CONDITION = (
    "${{ always() && steps.arm.outputs.armed == 'true' "
    "&& steps.live_smoke.outcome != 'success' }}"
)
FINAL_ASSERTION_NAME = "Require completed activation and live identity proof"


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


def load_workflow(path: Path) -> dict[str, object]:
    ruby = r'''
require "json"
require "yaml"
document = YAML.safe_load(File.read(ARGV.fetch(0)), aliases: true)
raise "workflow root must be a mapping" unless document.is_a?(Hash)
STDOUT.write(JSON.generate(document))
'''
    try:
        output = subprocess.check_output(
            ["ruby", "-rjson", "-ryaml", "-e", ruby, str(path)], text=True
        )
    except (OSError, subprocess.CalledProcessError) as error:
        fail(f"cannot structurally parse {path.name}: {error}")
    document = json.loads(output)
    if not isinstance(document, dict):
        fail(f"{path.name} root is not a mapping")
    return document


def checkout_refs(value: object) -> list[str]:
    refs: list[str] = []
    if isinstance(value, dict):
        for key, nested in value.items():
            if key == "uses" and isinstance(nested, str) and nested.startswith(
                "actions/checkout@"
            ):
                refs.append(nested.removeprefix("actions/checkout@"))
            refs.extend(checkout_refs(nested))
    elif isinstance(value, list):
        for nested in value:
            refs.extend(checkout_refs(nested))
    return refs


def require_pinned_checkout(
    workflow: dict[str, object], label: str, expected_count: int
) -> None:
    refs = checkout_refs(workflow)
    if len(refs) != expected_count:
        fail(f"{label} must contain exactly {expected_count} checkout action reference(s)")
    for ref in refs:
        if re.fullmatch(r"[0-9a-f]{40}", ref) is None:
            fail(f"{label} checkout action is not pinned to a full 40-character SHA")
        if ref != CHECKOUT_SHA:
            fail(f"{label} checkout action is not pinned to the reviewed repository SHA")


for checkout_canary in (
    "v4",
    "main",
    CHECKOUT_SHA[:-1],
    f"{CHECKOUT_SHA}0",
):
    if re.fullmatch(r"[0-9a-f]{40}", checkout_canary) is not None:
        fail(f"checkout pin validator accepted a non-40-character SHA canary: {checkout_canary}")

for recovery_condition_canary in (
    "${{ failure() }}",
    "${{ cancelled() }}",
    "${{ always() && steps.live_smoke.outcome != 'success' }}",
    "${{ always() && steps.arm.outputs.armed == 'true' }}",
):
    if recovery_condition_canary == FORWARD_FIX_CONDITION:
        fail("cancellation recovery validator accepted an incomplete-result canary")


def run_gate(body: str, **overrides: str) -> subprocess.CompletedProcess[str]:
    environment = {
        "PATH": "",
        "CONFIRM_ACTIVATION": "activate-jeeb-chat-staging",
        "DEFAULT_BRANCH": "main",
        "GITHUB_REF": "refs/heads/main",
        "GITHUB_REF_PROTECTED": "true",
        "GITHUB_REPOSITORY": "olivium-dev/jeeb-gateway",
        "REQUESTING_ACTOR": "oudaykhaled",
        "TARGET_ENVIRONMENT": "staging",
        "TRIGGERING_ACTOR": "oudaykhaled",
    }
    environment.update(overrides)
    return subprocess.run(
        ["/bin/bash", "--noprofile", "--norc", "-c", body],
        cwd=ROOT,
        env=environment,
        text=True,
        capture_output=True,
        check=False,
    )


document = load_workflow(WORKFLOW)
live_smoke_document = load_workflow(LIVE_SMOKE_WORKFLOW)
require_pinned_checkout(document, WORKFLOW_NAME, 1)
require_pinned_checkout(live_smoke_document, LIVE_SMOKE_WORKFLOW.name, 1)
expected_dispatch = {
    "workflow_dispatch": {
        "inputs": {
            "target_environment": {
                "description": "Protected Jeeb environment to activate",
                "required": True,
                "type": "choice",
                "options": ["staging", "production"],
            },
            "expected_uid": {
                "description": (
                    "Existing non-privileged Jeeb user id for the post-activation smoke"
                ),
                "required": True,
                "type": "string",
            },
            "confirm_activation": {
                "description": (
                    "Type activate-jeeb-chat-staging or activate-jeeb-chat-production"
                ),
                "required": True,
                "type": "string",
            },
        }
    }
}
if document.get("on") != expected_dispatch:
    fail("Chat B dispatch inputs or environment allowlist drifted")
if document.get("permissions") != {"checks": "read", "contents": "read"}:
    fail("Chat B permissions are not least-privilege")
if document.get("concurrency") != {
    "group": "jeeb-${{ inputs.target_environment }}-gateway-mutation",
    "cancel-in-progress": False,
}:
    fail("Chat B gateway mutation lock drifted")
jobs = document.get("jobs")
if not isinstance(jobs, dict) or set(jobs) != {"activation"}:
    fail("Chat B must keep activation and cancellation recovery in one running job")
activation_job = jobs["activation"]
if (
    not isinstance(activation_job, dict)
    or activation_job.get("environment") != "${{ inputs.target_environment }}"
):
    fail("Chat B activation job must use the selected protected GitHub environment")
if activation_job.get("if") != JOB_CONDITION:
    fail("Chat B activation job must remain running during cancellation")
if "continue-on-error" in activation_job:
    fail("Chat B activation job cannot bypass failure propagation")
steps = activation_job.get("steps")
if not isinstance(steps, list) or len(steps) != 11:
    fail("Chat B activation executable step set drifted")
if any(not isinstance(step, dict) for step in steps):
    fail("Chat B activation step is not a mapping")

gate = steps[0]
if set(gate) != {"name", "env", "run"} or gate.get("name") != GATE_NAME:
    fail("protected owner gate is not the first Chat B step")
gate_body = gate.get("run")
if not isinstance(gate_body, str):
    fail("protected owner gate has no executable body")
accepted = run_gate(gate_body)
if accepted.returncode != 0 or accepted.stdout or accepted.stderr:
    fail("valid protected staging owner gate was rejected")
accepted_production = run_gate(
    gate_body,
    TARGET_ENVIRONMENT="production",
    CONFIRM_ACTIVATION="activate-jeeb-chat-production",
)
if (
    accepted_production.returncode != 0
    or accepted_production.stdout
    or accepted_production.stderr
):
    fail("valid protected production owner gate was rejected")
for overrides in (
    {"GITHUB_REPOSITORY": "another/repository"},
    {"REQUESTING_ACTOR": "another-actor"},
    {"TRIGGERING_ACTOR": "rerun-by-another-actor"},
    {"GITHUB_REF": "refs/heads/feature"},
    {"GITHUB_REF_PROTECTED": "false"},
    {"TARGET_ENVIRONMENT": "development"},
    {"CONFIRM_ACTIVATION": "activate-jeeb-chat-production"},
):
    rejected = run_gate(gate_body, **overrides)
    if rejected.returncode == 0 or rejected.stdout or rejected.stderr:
        fail(f"protected owner gate accepted or exposed a negative case: {sorted(overrides)}")

if set(steps[1]) != {"uses"} or not str(steps[1]["uses"]).startswith("actions/checkout@"):
    fail("checkout is not immediately after the protected owner gate")

names = [str(step.get("name", "")) for step in steps]
for required_name in (
    "Validate exact Chat and Firebase contracts",
    "Require successful exact-SHA CI",
    "Install pinned cloudflared and configure strict SSH",
    "Prove exact host provider expand A1 and scoped relay key",
    "Arm cancellation-safe Chat B mutation",
    ACTIVATE_NAME,
    SMOKE_NAME,
    FORWARD_FIX_NAME,
    FINAL_ASSERTION_NAME,
):
    if names.count(required_name) != 1:
        fail(f"Chat B step count drifted: {required_name}")
preflight_index = names.index("Prove exact host provider expand A1 and scoped relay key")
arm_index = names.index("Arm cancellation-safe Chat B mutation")
activate_index = names.index(ACTIVATE_NAME)
smoke_index = names.index(SMOKE_NAME)
forward_fix_index = names.index(FORWARD_FIX_NAME)
final_assertion_index = names.index(FINAL_ASSERTION_NAME)
if not (
    preflight_index
    < arm_index
    < activate_index
    < smoke_index
    < forward_fix_index
    < final_assertion_index
):
    fail("Chat B arm/activation/smoke/recovery/assertion order drifted")

arm = steps[arm_index]
if set(arm) != {"name", "id", "run"} or arm.get("id") != "arm":
    fail("Chat B mutation arm step shape drifted")
if 'printf \'armed=true\\n\' >> "$GITHUB_OUTPUT"' not in str(arm.get("run", "")):
    fail("Chat B mutation is not durably armed before the remote update")
activate = steps[activate_index]
if set(activate) != {"name", "id", "env", "run"} or activate.get("id") != "activate":
    fail("Chat B activation step shape drifted")
smoke = steps[smoke_index]
if set(smoke) != {"name", "id", "env", "run"} or smoke.get("id") != "live_smoke":
    fail("Chat B live identity smoke step shape drifted")
forward_fix = steps[forward_fix_index]
if set(forward_fix) != {"name", "if", "env", "run"}:
    fail("Chat B cancellation recovery step shape drifted")
if forward_fix.get("if") != FORWARD_FIX_CONDITION:
    fail("Chat B recovery is not armed and cancellation-safe")
final_assertion = steps[final_assertion_index]
if set(final_assertion) != {"name", "if", "env", "run"}:
    fail("Chat B final required assertion step shape drifted")
if final_assertion.get("if") != JOB_CONDITION:
    fail("Chat B final required assertion does not run after cancellation or failure")
if final_assertion.get("env") != {
    "ACTIVATE_OUTCOME": "${{ steps.activate.outcome }}",
    "ARMED": "${{ steps.arm.outputs.armed }}",
    "LIVE_SMOKE_OUTCOME": "${{ steps.live_smoke.outcome }}",
}:
    fail("Chat B final assertion is not bound to arm, activation, and smoke outcomes")
assertion_body = str(final_assertion.get("run", ""))
for assertion in (
    '[ "$ARMED" = true ]',
    '[ "$ACTIVATE_OUTCOME" = success ]',
    '[ "$LIVE_SMOKE_OUTCOME" = success ]',
):
    if assertion not in assertion_body:
        fail(f"Chat B final required assertion is missing: {assertion}")
for index, step in enumerate(steps):
    if "continue-on-error" in step:
        fail(f"Chat B activation step {index} declares continue-on-error")
    condition = str(step.get("if", ""))
    if index not in {forward_fix_index, final_assertion_index} and condition:
        fail(f"Chat B activation step {index} declares an unreviewed condition")

source = WORKFLOW.read_text(encoding="utf-8")
normalized = source.replace("\\\n", " ")
if normalized.count("docker service update --detach=false") != 2:
    fail("Chat B must contain exactly activation and A1 forward-fix mutations")
for required in (
    "scripts/verify-chat-b-activation-preflight.sh",
    "FeatureFlags__UseUpstream__Chat=false",
    "FeatureFlags__UseUpstream__Chat=$desired",
    "--update-order start-first",
    "--update-parallelism 1",
    "--update-monitor 30s",
    "--update-failure-action pause",
    "--update-max-failure-ratio 0",
    "scripts/smoke-jeeb-firebase-token-exchange.py",
    "JEEB_PUSH_GATEWAY_API_KEY: ${{ secrets.JEEB_PUSH_GATEWAY_API_KEY }}",
    "JEEB_TOKEN_MINT_KEY: ${{ secrets.JEEB_TOKEN_MINT_KEY }}",
    "JEEB_FIREBASE_WEB_API_KEY: ${{ secrets.JEEB_FIREBASE_WEB_API_KEY }}",
    "if: ${{ always() }}",
    "id: arm",
    (
        "if: ${{ always() && steps.arm.outputs.armed == 'true' "
        "&& steps.live_smoke.outcome != 'success' }}"
    ),
    "ACTIVATE_OUTCOME: ${{ steps.activate.outcome }}",
    "LIVE_SMOKE_OUTCOME: ${{ steps.live_smoke.outcome }}",
    'if [ "${#current_flags[@]}" -ne 1 ]',
    '|| [ "${current_flags[0]:-}" != FeatureFlags__UseUpstream__Chat=false ]; then',
):
    if required not in source:
        fail(f"Chat B contract marker is missing: {required}")
for forbidden in (
    "docker service " + "create",
    "docker service " + "rm",
    "docker service " + "rollback",
    "--image",
    "--update-failure-action " + "rollback",
    "--" + "rollback-order",
    "set -x",
    "continue-on-error:",
):
    if forbidden in source:
        fail(f"Chat B contains forbidden mutation or secret surface: {forbidden}")
if source.count("--env-add \"FeatureFlags__UseUpstream__Chat=$desired\"") != 2:
    fail("Chat B activation and A1 forward-fix do not share exact flag reconciliation")
if source.count("--env-rm \"$key\"") != 2:
    fail("Chat B does not remove every stale/case-variant flag before each update")
if "--expected-uid \"$EXPECTED_UID\"" not in source:
    fail("Chat B live exchange does not bind the expected Firebase uid")

preflight_source = (ROOT / "scripts/verify-chat-b-activation-preflight.sh").read_text(
    encoding="utf-8"
)
for required in (
    "PUSH_AUTH_MODE=expand",
    "jeeb-staging-push-notification",
    "push-notification",
    "/run/secrets/push_gateway_api_key",
    "PushNotificationServiceApi__BaseUrl=$expected_provider_url",
    "/api/v1/register/ready",
    'scope == "gateway.registration"',
    ".File.Mode",
    '[ "$key_mode" = 256 ]',
):
    if required not in preflight_source:
        fail(f"Chat B provider preflight marker is missing: {required}")

print("Jeeb Chat B protected preflight, activation, smoke, and A1 forward-fix authority: PASS")
