#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

python3 - <<'PY'
import re
from pathlib import Path

workflow_path = Path(".github/workflows/jeeb-staging-deploy.yml")
a1_path = Path("deploy/staging-gateway/a1-bootstrap.env")
b_path = Path("deploy/staging-gateway/b-activation.env")


def read_contract(path):
    rows = {}
    for line_number, raw in enumerate(path.read_text().splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            raise SystemExit(f"FAIL: malformed phase contract {path}:{line_number}")
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", key) or not value:
            raise SystemExit(f"FAIL: unsafe phase contract row {path}:{line_number}")
        lowered = key.lower()
        if lowered in rows:
            raise SystemExit(f"FAIL: duplicate phase contract key {path}:{line_number}: {key}")
        rows[lowered] = (key, value)
    return rows


expected_a1 = {
    "ASPNETCORE_ENVIRONMENT": "Staging",
    "JeebFirebaseContract__SchemaVersion": "1",
    "JeebFirebaseContract__ProjectId": "jeeb-5a293",
    "JeebFirebaseContract__ProjectNumber": "1051234312170",
    "JeebFirebaseContract__FirestoreDatabaseId": "(default)",
    "JeebFirebaseContract__ChatEnabled": "true",
    "JeebFirebaseContract__PushProducer": "notification-service",
    "Firebase__Chat__ProjectId": "jeeb-5a293",
    "Firebase__Chat__ServiceAccountKeyPath": "/run/secrets/firebase_admin_json",
    "FeatureFlags__NotificationDurableWrite__Enabled": "true",
    "FeatureFlags__NotificationOutboxMode": "upstream-authority",
    "FeatureFlags__PushDispatchMode": "local",
    "DemoUsers__Enabled": "true",
    "Features__DevEndpoints__Enabled": "true",
    "Features__Swagger__Enabled": "true",
    # A1/B are the two PHASE DOCUMENTS (deploy/staging-gateway/*.env): A1 describes chat off,
    # B describes chat on. Neither is a deploy-time pin — the workflow and the candidate
    # contract both bind the RESOLVED state, asserted below.
    "FeatureFlags__UseUpstream__Chat": "false",
    "FeatureFlags__UseUpstream__Otp": "true",
    "FeatureFlags__UseUpstream__Realtime": "false",
    "Features__RealtimeWebSocketProxy__Enabled": "false",
    "FeatureFlags__UseUpstream__Voice": "false",
    "Services__ServiceOTP__BaseUrl": "http://jeeb-staging-one-time-password:8080",
    "ServiceOTPApi__BaseUrl": "http://jeeb-staging-one-time-password:8080",
    "Auth__Otp__ApplicationId": "0d51afe1-499f-4a29-a55a-36d2dd223b05",
    "Auth__Otp__Phone__AllowedRegion": "LB",
    "Auth__Otp__Phone__EnforceRegion": "false",
    "Services__Realtime__BaseUrl": "http://jeeb-staging-realtime-comunication-service:4000",
    "Operations__RealtimeProbe__MintKeyFile": "/run/secrets/staging_wss_probe_mint_key",
    "SuperLogin__OpenMode": "true",
}
expected_b = dict(expected_a1)
for activated in (
    "FeatureFlags__UseUpstream__Chat",
    "FeatureFlags__UseUpstream__Realtime",
    "Features__RealtimeWebSocketProxy__Enabled",
):
    expected_b[activated] = "true"


def canonical(rows):
    return {key: value for key, value in (row for row in rows.values())}


a1 = canonical(read_contract(a1_path))
b = canonical(read_contract(b_path))
if a1 != expected_a1:
    raise SystemExit(f"FAIL: A1 phase contract drifted: {a1!r}")
if b != expected_b:
    raise SystemExit(f"FAIL: B phase contract drifted: {b!r}")

workflow = workflow_path.read_text()
dispatch_header = workflow[: workflow.index("permissions:")]
expected_dispatch = '''name: jeeb-staging-deploy

"on":
  workflow_dispatch:
    inputs:
      deployment_mode:
        description: Protected staging deployment mode
        required: true
        default: normal
        type: choice
        options:
          - normal
          - security-cutover
          - otp-cutover
          - devtool-reassert
      provider_expand_verified:
        description: 'Confirm protected-main push-notification is live and verified in expand mode'
        required: true
        type: boolean
        default: false

'''
if dispatch_header != expected_dispatch:
    raise SystemExit("FAIL: staging deployment-mode dispatch contract drifted")
input_references = set(re.findall(r"\binputs\.([A-Za-z][A-Za-z0-9_]*)", workflow))
if input_references != {"deployment_mode", "provider_expand_verified"}:
    raise SystemExit(
        f"FAIL: staging workflow exposes unexpected callable inputs: {sorted(input_references)}"
    )

# Chat is the one phase flag the deploy must NOT pin to a literal: a hardcoded
# false reverted every completed Chat B activation on the next deploy.
CHAT_KEY = "FeatureFlags__UseUpstream__Chat"
for key, value in expected_a1.items():
    if key == CHAT_KEY:
        continue
    markers = (
        f"add_env {key} {value}",
        f"add_env {key} '{value}'",
        f'add_env {key} "{value}"',
    )
    count = sum(workflow.count(marker) for marker in markers)
    if count != 1:
        raise SystemExit(
            f"FAIL: A1 workflow does not bind exactly one {key}={value!r} row"
        )

if workflow.count(f'add_env {CHAT_KEY} "$chat_upstream_enabled"') != 1:
    raise SystemExit(
        f"FAIL: staging workflow does not bind exactly one resolved {CHAT_KEY} row"
    )
for literal in ("true", "false", "'true'", "'false'", '"true"', '"false"'):
    if f"add_env {CHAT_KEY} {literal}" in workflow:
        raise SystemExit(
            f"FAIL: staging workflow hardcodes {CHAT_KEY}={literal} instead of the"
            " resolved single source of truth"
        )
for marker in (
    "JEEB_STAGING_CHAT_ENABLED: ${{ vars.JEEB_STAGING_CHAT_ENABLED }}",
    'chat_upstream_declared="${JEEB_STAGING_CHAT_ENABLED:-}"',
    "scripts/read-staging-chat-flag.sh",
    '"$chat_upstream_enabled" <<\'FLAGS\'',
    f'"{CHAT_KEY}=$chat_upstream_enabled"',
):
    if marker not in workflow:
        raise SystemExit(f"FAIL: chat activation single-source-of-truth marker missing: {marker}")
if workflow.count("chat_upstream_enabled=") != 4:
    raise SystemExit("FAIL: chat activation resolver assignment set drifted")

for activated in (
    "FeatureFlags__UseUpstream__Realtime",
    "Features__RealtimeWebSocketProxy__Enabled",
):
    if f"add_env {activated} true" in workflow:
        raise SystemExit(f"FAIL: B activation leaked into A1 workflow: {activated}")

if set(a1) != set(b):
    raise SystemExit("FAIL: A1 and B configuration key sets differ")
deltas = {key for key in a1 if a1[key] != b[key]}
expected_deltas = {
    "FeatureFlags__UseUpstream__Chat",
    "FeatureFlags__UseUpstream__Realtime",
    "Features__RealtimeWebSocketProxy__Enabled",
}
if deltas != expected_deltas:
    raise SystemExit(f"FAIL: B activation delta is not flag-only: {sorted(deltas)}")

# The deploy-time candidate contract must ACCEPT whichever chat state the deploy resolved.
# A literal pin here is a gate no activated candidate can pass: it rejected the Spec with a
# silent `jq -e` exit 1 and killed run 33821087895 with zero diagnostic output.
candidate_contract = Path("scripts/staging-gateway-candidate-contract.jq").read_text()
if '"featureflags__useupstream__chat": $chat_upstream_enabled,' not in candidate_contract:
    raise SystemExit(
        "FAIL: the candidate contract does not bind chat to the resolved $chat_upstream_enabled")
for literal in ('"false"', '"true"'):
    if f'"featureflags__useupstream__chat": {literal}' in candidate_contract:
        raise SystemExit(f"FAIL: the candidate contract pins chat to the literal {literal}")
if '$environment["featureflags__useupstream__chat"] ==' in candidate_contract:
    raise SystemExit("FAIL: the candidate contract compares chat outside the expectation table")
for marker in (
    '--arg chat_upstream_enabled "$chat_upstream_enabled"',
    'RED: candidate contract rejected',
    'candidate_contract -r true >&2',
):
    if marker not in workflow:
        raise SystemExit(f"FAIL: candidate-contract diagnostic marker missing: {marker}")

print("Staging gateway A1 bootstrap and separate B activation contracts are exact.")
PY
