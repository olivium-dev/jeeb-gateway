#!/usr/bin/env python3
"""Fail closed on Jeeb Firebase/chat/push configuration drift."""

from __future__ import annotations

import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CONTRACT_PATH = ROOT / "contracts/jeeb-firebase-v1.json"

EXPECTED = {
    "schemaVersion": 1,
    "projectId": "jeeb-5a293",
    "projectNumber": "1051234312170",
    "firestoreDatabaseId": "(default)",
    "chatEnabled": True,
    "pushProducer": "notification-service",
}

CONTRACT_ENV = {
    "JeebFirebaseContract__SchemaVersion": "1",
    "JeebFirebaseContract__ProjectId": EXPECTED["projectId"],
    "JeebFirebaseContract__ProjectNumber": EXPECTED["projectNumber"],
    "JeebFirebaseContract__FirestoreDatabaseId": EXPECTED["firestoreDatabaseId"],
    "JeebFirebaseContract__ChatEnabled": "true",
    "JeebFirebaseContract__PushProducer": EXPECTED["pushProducer"],
    "Firebase__Chat__ProjectId": EXPECTED["projectId"],
    "Firebase__Chat__ServiceAccountKeyPath": "/run/secrets/firebase_admin_json",
    "FeatureFlags__NotificationDurableWrite__Enabled": "true",
    "FeatureFlags__NotificationOutboxMode": "upstream-authority",
    # Despite its historical name, the local rung is the notification-service
    # hand-over. The upstream-authority rung attempts gateway -> push relay and
    # is permanently denied by GatewayDirectPushDispatchGuardHandler.
    "FeatureFlags__PushDispatchMode": "local",
}

LEGACY_DATABASE_KEYS = (
    "Firestore__DatabaseId",
    "Firebase__FirestoreDatabaseId",
    "Firebase__Chat__FirestoreDatabaseId",
)


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


def read_json(path: Path) -> dict[str, object]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read JSON {path.relative_to(ROOT)}: {error}")
    if not isinstance(document, dict):
        fail(f"{path.relative_to(ROOT)} must contain one JSON object")
    return document


def read_env(path: Path) -> dict[str, str]:
    rows: dict[str, str] = {}
    for line_number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            fail(f"malformed env row {path.relative_to(ROOT)}:{line_number}")
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", key) or not value:
            fail(f"unsafe env row {path.relative_to(ROOT)}:{line_number}")
        lowered = key.lower()
        if any(existing.lower() == lowered for existing in rows):
            fail(f"duplicate env key {path.relative_to(ROOT)}:{line_number}: {key}")
        rows[key] = value
    return rows


def require_env(rows: dict[str, str], expected: dict[str, str], label: str) -> None:
    for key, value in expected.items():
        if rows.get(key) != value:
            fail(f"{label} must set {key}={value!r}, got {rows.get(key)!r}")


contract = read_json(CONTRACT_PATH)
if contract != EXPECTED or list(contract) != list(EXPECTED):
    fail("contracts/jeeb-firebase-v1.json schema/value/order drifted")
canonical_text = json.dumps(EXPECTED, indent=2, ensure_ascii=False) + "\n"
if CONTRACT_PATH.read_text(encoding="utf-8") != canonical_text:
    fail("contracts/jeeb-firebase-v1.json must be canonical two-space JSON with a final newline")

base = read_json(ROOT / "src/JeebGateway/appsettings.json")
development = read_json(ROOT / "src/JeebGateway/appsettings.Development.json")
production = read_json(ROOT / "src/JeebGateway/appsettings.Production.json")
if base.get("JeebFirebaseContract") != EXPECTED:
    fail("base runtime JeebFirebaseContract does not match the cross-repo contract")
if development.get("JeebFirebaseContract") is not None:
    fail("development must inherit, not override, the canonical Firebase contract")
if development.get("Firebase") is not None:
    fail("development must not override the canonical Firebase identity")
if base.get("Firebase", {}).get("Chat", {}).get("ProjectId") != EXPECTED["projectId"]:
    fail("Firebase:Chat:ProjectId does not match the canonical project")
if base.get("FeatureFlags", {}).get("UseUpstream", {}).get("Chat") is not False:
    fail("the shared development/test default must keep upstream Chat disabled")

production_flags = production.get("FeatureFlags", {})
production_expected = {
    "NotificationDurableWrite": {"Enabled": True},
    "NotificationOutboxMode": "upstream-authority",
    "PushDispatchMode": "local",
}
for key, value in production_expected.items():
    if production_flags.get(key) != value:
        fail(f"production runtime {key} must be {value!r}")
if production_flags.get("UseUpstream", {}).get("Chat") is not True:
    fail("production coordinated target must explicitly enable upstream Chat")


def inspect_database_keys(value: object, path: tuple[str, ...] = ()) -> None:
    if isinstance(value, dict):
        for key, nested in value.items():
            child = (*path, key)
            if key.lower() in {"databaseid", "firestoredatabaseid"}:
                if child != ("JeebFirebaseContract", "firestoreDatabaseId"):
                    fail(f"legacy Firestore selector is forbidden in appsettings: {':'.join(child)}")
                if nested != EXPECTED["firestoreDatabaseId"]:
                    fail("runtime Firestore database must remain (default)")
            inspect_database_keys(nested, child)
    elif isinstance(value, list):
        for nested in value:
            inspect_database_keys(nested, path)


for document in (base, development, production):
    inspect_database_keys(document)

a1 = read_env(ROOT / "deploy/staging-gateway/a1-bootstrap.env")
b = read_env(ROOT / "deploy/staging-gateway/b-activation.env")
require_env(a1, CONTRACT_ENV, "staging A1 bootstrap")
require_env(b, CONTRACT_ENV, "staging B activation")
if a1.get("FeatureFlags__UseUpstream__Chat") != "false":
    fail("staging A1 bootstrap must not activate Chat")
if b.get("FeatureFlags__UseUpstream__Chat") != "true":
    fail("only the separately reviewed staging B target may activate Chat")

workflows = {
    name: (ROOT / ".github/workflows" / name).read_text(encoding="utf-8")
    for name in (
        "deploy-to-jeeb.yml",
        "jeeb-production-deploy.yml",
        "jeeb-staging-deploy.yml",
    )
}

live_smoke_workflow = (
    ROOT / ".github/workflows/jeeb-chat-firebase-live-smoke.yml"
).read_text(encoding="utf-8")
chat_b_workflow = (ROOT / ".github/workflows/jeeb-chat-b-activation.yml").read_text(
    encoding="utf-8"
)

for name, source in workflows.items():
    if "scripts/validate-jeeb-firebase-contract.py" not in source:
        fail(f"{name} does not run the Firebase contract gate")
    for key in LEGACY_DATABASE_KEYS:
        if f"--env-add {key}" in source or f"add_env {key} " in source:
            fail(f"{name} reintroduces legacy database selector {key}")
        if key.lower() not in source.lower():
            fail(f"{name} does not reconcile stale {key}")
    for marker in (
        "secrets.JEEB_FIREBASE_JSON",
        "scripts/validate-firebase-service-account.py",
        "/run/secrets/firebase_admin_json",
        "firebase_admin_json",
    ):
        if marker not in source:
            fail(f"{name} does not enforce Firebase credential marker {marker}")

content_addressed_prefix = {
    "deploy-to-jeeb.yml": "jeeb_fb_ \"$firebase_digest\"",
    "jeeb-production-deploy.yml": "jeeb_production_fb_ \"$firebase_digest\"",
    "jeeb-staging-deploy.yml": "jeeb_staging_fb_ \"$firebase_digest\"",
}
for name, prefix in content_addressed_prefix.items():
    if prefix not in workflows[name]:
        fail(f"{name} does not content-address Firebase credential rotation")
    if "scripts/firebase-docker-secret-name.sh" not in workflows[name]:
        fail(f"{name} does not enforce the bounded Docker secret-name contract")

for name in ("deploy-to-jeeb.yml", "jeeb-production-deploy.yml"):
    source = workflows[name]
    for key, value in CONTRACT_ENV.items():
        if f"{key}={value}" not in source and f"{key}='{value}'" not in source:
            fail(f"{name} does not pin {key}={value}")
    if "FeatureFlags__UseUpstream__Chat=true" not in source and "FeatureFlags__UseUpstream__Chat='true'" not in source:
        fail(f"{name} does not pin coordinated Chat=true")

direct = workflows["deploy-to-jeeb.yml"]
if "useupstreamchat)" in direct.lower():
    fail("deploy-to-jeeb exposes canonical Chat activation as an operator override")

staging = workflows["jeeb-staging-deploy.yml"]
for key, value in CONTRACT_ENV.items():
    accepted_rows = (
        f"add_env {key} {value}",
        f"add_env {key} '{value}'",
        f'add_env {key} "{value}"',
    )
    if not any(row in staging for row in accepted_rows):
        fail(f"staging deploy does not reconcile {key}={value}")
# B activation stays a separate reviewed decision, but the deploy must carry the
# resolved state forward instead of hardcoding false and reverting it every run.
if 'add_env FeatureFlags__UseUpstream__Chat "$chat_upstream_enabled"' not in staging:
    fail("staging deploy does not bind Chat to the resolved activation state")
for literal in ("true", "false", "'true'", "'false'", '"true"', '"false"'):
    if f"add_env FeatureFlags__UseUpstream__Chat {literal}" in staging:
        fail(f"staging deploy hardcodes Chat={literal} instead of resolving it")
if "($removed_env + $replaced_env)" not in staging:
    fail("staging candidate must replace desired env keys case-insensitively")
if 'gsub(":"; "__")' not in staging:
    fail("staging candidate does not reconcile colon-delimited configuration aliases")
for name in ("deploy-to-jeeb.yml", "jeeb-production-deploy.yml"):
    if "sed 's/:/__/g'" not in workflows[name]:
        fail(f"{name} does not reconcile colon-delimited configuration aliases")

ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
if "python3 scripts/validate-jeeb-firebase-contract.py" not in ci:
    fail("CI does not execute the Jeeb Firebase contract gate")
if "bash scripts/test-validate-firebase-service-account.sh" not in ci:
    fail("CI does not execute Firebase credential validation regression tests")
if "python3 scripts/test-smoke-jeeb-firebase-token-exchange.py" not in ci:
    fail("CI does not execute the Firebase live-smoke offline contract tests")
if "bash scripts/test-chat-b-activation-preflight.sh" not in ci:
    fail("CI does not execute the Chat B relay preflight regression tests")
if "python3 scripts/test-chat-b-activation-cancellation-contract.py" not in ci:
    fail("CI does not execute the Chat B cancellation and checkout negative controls")

for marker in (
    "workflow_dispatch:",
    "environment: ${{ inputs.target_environment }}",
    "GITHUB_REF_PROTECTED: ${{ github.ref_protected }}",
    "REQUESTING_ACTOR: ${{ github.actor }}",
    "TRIGGERING_ACTOR: ${{ github.triggering_actor }}",
    "secrets.JEEB_TOKEN_MINT_KEY",
    "secrets.JEEB_FIREBASE_WEB_API_KEY",
    "scripts/smoke-jeeb-firebase-token-exchange.py",
    "https://app.jeeb.fds-1.com",
    "https://jeeb.fds-1.com",
):
    if marker not in live_smoke_workflow:
        fail(f"protected Firebase live-smoke workflow is missing {marker}")
for forbidden_smoke_surface in (
    "set -x",
    "echo $JEEB_TOKEN_MINT_KEY",
    "echo $JEEB_FIREBASE_WEB_API_KEY",
    "--data",
    "curl ",
):
    if forbidden_smoke_surface in live_smoke_workflow:
        fail(f"Firebase live-smoke workflow exposes an unsafe surface: {forbidden_smoke_surface}")

smoke_script = (ROOT / "scripts/smoke-jeeb-firebase-token-exchange.py").read_text(
    encoding="utf-8"
)
for marker in (
    "/auth/tokens",
    "/v1/chat/firebase-token",
    "accounts:signInWithCustomToken",
    'exchanged.get("localId") != uid',
):
    if marker not in smoke_script:
        fail(f"Firebase live-smoke identity chain is missing {marker}")
for forbidden_output in (
    "print(bearer",
    "print(custom_token",
    "print(firebase_api_key",
    "print(exchanged",
):
    if forbidden_output in smoke_script:
        fail(f"Firebase live-smoke may expose a credential: {forbidden_output}")

for marker in (
    "environment: ${{ inputs.target_environment }}",
    "scripts/verify-chat-b-activation-preflight.sh",
    "FeatureFlags__UseUpstream__Chat=false",
    "--update-failure-action pause",
    "if: ${{ always() && steps.arm.outputs.armed == 'true' && steps.live_smoke.outcome != 'success' }}",
    "Forward-fix Chat to A1 after any incomplete activation",
    "Require completed activation and live identity proof",
    "scripts/smoke-jeeb-firebase-token-exchange.py",
):
    if marker not in chat_b_workflow:
        fail(f"distinct protected Chat B authority is missing {marker}")
for forbidden_chat_b in (
    "docker service " + "create",
    "docker service " + "rm",
    "docker service " + "rollback",
    "--update-failure-action " + "rollback",
):
    if forbidden_chat_b in chat_b_workflow:
        fail(f"Chat B authority violates forward-only policy: {forbidden_chat_b}")

program = (ROOT / "src/JeebGateway/Program.cs").read_text(encoding="utf-8")
options = (ROOT / "src/JeebGateway/Configuration/JeebFirebaseContractOptions.cs").read_text(
    encoding="utf-8"
)
guard = (ROOT / "src/JeebGateway/Services/Clients/GatewayDirectPushDispatchGuardHandler.cs").read_text(
    encoding="utf-8"
)
for token in ("BindConfiguration", "IsCanonical", "HasNoConflictingDatabaseOverride", "ValidateOnStart"):
    if token not in program:
        fail(f"runtime startup Firebase validation is missing {token}")
for token in (
    "FirebaseCustomTokenStartupValidator",
    "ValidateConfiguration",
    "AddHostedService",
):
    if token not in program and token not in (
        ROOT / "src/JeebGateway/Chat/Firebase/FirebaseCustomTokenMinter.cs"
    ).read_text(encoding="utf-8"):
        fail(f"runtime eager Firebase credential validation is missing {token}")
if 'FeatureFlags:PushDispatchMode is pinned to \\"local\\" (ADR-0013)' not in program:
    fail("runtime startup no longer rejects a stale gateway direct-push rung")
for value in (EXPECTED["projectId"], EXPECTED["projectNumber"], EXPECTED["firestoreDatabaseId"], EXPECTED["pushProducer"]):
    if str(value) not in options:
        fail(f"runtime options omit canonical value {value}")
for route in ("/device/", "/user/", "/broadcast", "/topic/"):
    if route not in guard:
        fail(f"gateway direct-push guard no longer blocks {route}")
if "IConfiguration" in guard or "IOptions" in guard:
    fail("gateway direct-push denial must not be configurable")

print("Jeeb Firebase v1 contract, runtime identity, staged Chat activation, and push ownership are exact.")
