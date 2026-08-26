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
    "DemoUsers__Enabled": "false",
    "FeatureFlags__UseUpstream__Chat": "false",
    "FeatureFlags__UseUpstream__Otp": "true",
    "FeatureFlags__UseUpstream__Realtime": "false",
    "Features__RealtimeWebSocketProxy__Enabled": "false",
    "FeatureFlags__UseUpstream__Voice": "false",
    "Services__ServiceOTP__BaseUrl": "http://jeeb-staging-one-time-password:8080",
    "ServiceOTPApi__BaseUrl": "http://jeeb-staging-one-time-password:8080",
    "Auth__Otp__ApplicationId": "0d51afe1-499f-4a29-a55a-36d2dd223b05",
    "Auth__Otp__Phone__AllowedRegion": "LB",
    "Auth__Otp__Phone__EnforceRegion": "true",
    "Services__Realtime__BaseUrl": "http://jeeb-staging-realtime-comunication-service:4000",
    "Operations__RealtimeProbe__MintKeyFile": "/run/secrets/staging_wss_probe_mint_key",
    "SuperLogin__OpenMode": "false",
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

'''
if dispatch_header != expected_dispatch:
    raise SystemExit("FAIL: staging deployment-mode dispatch contract drifted")
input_references = set(
    re.findall(r"\$\{\{\s*inputs\.([A-Za-z][A-Za-z0-9_]*)", workflow)
)
if input_references != {"deployment_mode"}:
    raise SystemExit(
        f"FAIL: staging workflow exposes unexpected callable inputs: {sorted(input_references)}"
    )

for key, value in expected_a1.items():
    marker = f"add_env {key} {value}"
    if workflow.count(marker) != 1:
        raise SystemExit(f"FAIL: A1 workflow does not bind exactly one {marker!r}")

for activated in (
    "FeatureFlags__UseUpstream__Chat",
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

print("Staging gateway A1 bootstrap and separate B activation contracts are exact.")
PY
