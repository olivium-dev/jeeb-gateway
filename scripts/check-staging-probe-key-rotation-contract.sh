#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
from pathlib import Path

workflow = Path(".github/workflows/jeeb-staging-probe-key-rotate.yml").read_text()
remote = Path(".github/scripts/rotate-staging-gateway-probe-key.sh").read_text()

required_workflow = (
    'workflow_dispatch:',
    'environment: staging',
    'GITHUB_REF_PROTECTED',
    'refs/heads/${DEFAULT_BRANCH}',
    'JEEB_STAGING_WSS_PROBE_MINT_KEY: ${{ secrets.JEEB_STAGING_WSS_PROBE_MINT_KEY }}',
    'ghcr.io/olivium-dev/jeeb-gateway@sha256:',
    'source scripts/staging-gateway-mutation-lock.sh',
    'staging_gateway_lock_acquire',
    'docker secret create',
    'scripts/probe-staging-authenticated-realtime.py',
    'direct_descriptor_http_status: 200',
    'credential_log_leak_count: 0',
)
required_remote = (
    '--with-registry-auth',
    '--update-order start-first',
    '--update-failure-action ' + 'rollback',
    '--update-parallelism 1',
    '--secret-rm "$old_probe_secret"',
    'staging_wss_probe_mint_key',
    'FeatureFlags__UseUpstream__Otp true',
    'FeatureFlags__UseUpstream__Chat true',
    'FeatureFlags__UseUpstream__Realtime true',
    'Features__RealtimeWebSocketProxy__Enabled true',
    'FeatureFlags__UseUpstream__Voice false',
    'http://jeeb-staging-realtime-comunication-service:4000',
    'CANDIDATE_HEALTHY_WITH_INCUMBENT=1',
)
for token in required_workflow:
    if token not in workflow:
        raise SystemExit(f"rotation workflow contract missing: {token}")
for token in required_remote:
    if token not in remote:
        raise SystemExit(f"rotation remote contract missing: {token}")

forbidden = (
    "docker service " + "rm",
    "docker service create",
    "StrictHostKeyChecking no",
    "StrictHostKeyChecking=no",
    ":latest",
)
combined = workflow + remote
exact_probe_source_selector = (
    '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}'
    '{{if eq .File.Name "staging_wss_probe_mint_key"}}'
    '{{println .SecretName}}{{end}}{{end}}'
)
if combined.count(exact_probe_source_selector) != 2:
    raise SystemExit("rotation contract does not use the exact probe-secret target selector twice")
if "awk -F' \\| '" in combined:
    raise SystemExit("rotation contract reintroduced the ambiguous ERE field separator")
for token in forbidden:
    if token in combined:
        raise SystemExit(f"rotation contract contains forbidden token: {token}")

if "192.168.2." + "50" in combined:
    raise SystemExit("rotation contract contains retired-host destination")
if "10069" in combined:
    raise SystemExit("rotation contract bypasses the gateway to the realtime host port")
if "continue-on-error" in combined:
    raise SystemExit("rotation contract contains a fail-open step")

print("staging probe-key protected rotation contract: PASS")
PY
