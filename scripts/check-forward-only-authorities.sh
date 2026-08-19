#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

python3 - <<'PY'
import json
import re
import subprocess
from pathlib import Path


def tracked_utf8():
    names = subprocess.check_output(["git", "ls-files", "-z"]).split(b"\0")
    for raw_name in names:
        if not raw_name:
            continue
        path = Path(raw_name.decode("utf-8", "surrogateescape"))
        try:
            data = path.read_bytes()
        except FileNotFoundError:
            continue
        if b"\0" in data:
            continue
        try:
            yield path, data.decode("utf-8")
        except UnicodeDecodeError:
            continue


forbidden = {
    "service deletion": re.compile(
        r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+service\s+" + r"rm\b",
        re.I,
    ),
    "service rollback": re.compile(r"docker\s+service\s+" + r"rollback\b", re.I),
    "unsafe service scale": re.compile(
        r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+service\s+scale\b"
        r"[^\n]*=\s*(?:0|[2-9][0-9]*)\b", re.I
    ),
    "automatic rollback": re.compile(r"--update-failure-action(?:=|\s+)" + r"rollback\b", re.I),
    "rollback option": re.compile(r"--" + r"rollback(?:-[a-z-]+)?\b", re.I),
    "schema downgrade command": re.compile(
        r"\b(?:alembic\s+" + "down" + r"grade|goose\s+" + "down" +
        r"|migrate\s+" + "down" + r")\b", re.I
    ),
    "retired undo pointer": re.compile("/" + "UNDO-"),
    "operator rollback instruction": re.compile(r"roll\s+" + r"back\s+otherwise", re.I),
    "database snapshot or restore": re.compile(r"\b" + "pg_" + r"(?:dump|restore)\b", re.I),
    "Git prior-state recovery": re.compile(
        r"\bgit\s+(?:re" + "vert" + r"\b|reset\b[^\n]*--hard\b"
        r"|check" + "out" + r"\b"
        r"|restore\b[^\n]*--source\b|switch\b[^\n]*--detach\b)", re.I
    ),
    "floating deployment image alias": re.compile(
        r"\b[a-z0-9._/-]+:" + r"(?:latest|main|master|dev|staging|prod|production)\b",
        re.I,
    ),
    "mutable literal Swarm image": re.compile(
        r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+service\s+"
        r"(?:update|create)\b[^\n]*--image(?:=|\s+)\s*[\"']?"
        r"[a-z0-9._/-]+:[a-z0-9._-]+\b(?!@sha256:)",
        re.I,
    ),
    "historical deployment authority": re.compile(
        r"\b(?:previous|prior|predecessor)\s+(?:image|container|service|deployment|database|dump)\b",
        re.I,
    ),
}

adversarial_canaries = {
    "variable Docker deletion": 'DOCKER=docker; "$DOCKER" service ' + "rm app",
    "variable Git prior selector": "git check" + 'out "$PREVIOUS_SHA"',
    "mutable Swarm image": (
        "docker service update --image ghcr.io/olivium-dev/app:" + "production app"
    ),
    "unsafe service scale": "docker service " + "scale app=0",
}
for description, canary in adversarial_canaries.items():
    if not any(pattern.search(canary) for pattern in forbidden.values()):
        raise SystemExit(f"FAIL: scanner does not reject adversarial canary: {description}")

violations = []
utf8_count = 0
tracked = list(tracked_utf8())
for path, text in tracked:
    utf8_count += 1
    for line_number, line in enumerate(text.splitlines(), 1):
        for label, pattern in forbidden.items():
            if (
                label == "Git prior-state recovery"
                and path == Path("tests/gw3-pack/neg-controls.sh")
                and line.strip().startswith("u_N17()")
            ):
                continue
            if pattern.search(line):
                violations.append(f"{path}:{line_number}: {label}: {line.strip()}")

if violations:
    raise SystemExit(
        "FAIL: tracked UTF-8 files contain destructive/reversion operations:\n"
        + "\n".join(violations)
    )

workflow_dir = Path(".github/workflows")
deploy_inventory = {
    path.name for path in workflow_dir.glob("*deploy*.yml")
}
expected_inventory = {
    "deploy-to-jeeb.yml",
    "jeeb-staging-deploy.yml",
}
if deploy_inventory != expected_inventory:
    raise SystemExit(
        "FAIL: deploy workflow inventory drifted: "
        f"actual={sorted(deploy_inventory)} expected={sorted(expected_inventory)}"
    )

mutation_pattern = re.compile(
    r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+service\s+"
    r"(?:update|create|scale|" + "rm|rollback" + r")\b", re.I
)
for canary in (
    'ENGINE=docker\n"$ENGINE" service ' + 'update --image "$IMAGE" app',
    "docker service " + "scale app=0",
):
    if not mutation_pattern.search(canary):
        raise SystemExit("FAIL: service mutation inventory misses an adversarial canary")
mutation_inventory = {
    path.relative_to(Path(".")).as_posix()
    for root in (Path(".github/workflows"), Path(".github/scripts"))
    for path in root.rglob("*")
    if path.is_file() and path.suffix in {".yml", ".yaml", ".sh"}
    and mutation_pattern.search(path.read_text())
}
expected_mutation_inventory = {
    ".github/scripts/jeeb-gateway-secret-lifecycle.sh",
    ".github/workflows/deploy-to-jeeb.yml",
    ".github/workflows/jeeb-staging-deploy.yml",
    ".github/workflows/jeeb-staging-state-auth-smoke.yml",
}
if mutation_inventory != expected_mutation_inventory:
    raise SystemExit(
        "FAIL: service mutation inventory drifted: "
        f"actual={sorted(mutation_inventory)} expected={sorted(expected_mutation_inventory)}"
    )

lifecycle = Path(".github/scripts/jeeb-gateway-secret-lifecycle.sh").read_text()
if ".Previous" + "Spec" in lifecycle:
    raise SystemExit("FAIL: secret lifecycle reads retired service-spec metadata")
smoke = Path(".github/workflows/jeeb-staging-state-auth-smoke.yml").read_text()
if "scripts/verify-swarm-service-image.sh" not in smoke or "--image" in smoke:
    raise SystemExit("FAIL: state-auth restart route lacks exact current-image verification")

deploy_text = {name: (workflow_dir / name).read_text() for name in expected_inventory}
for name, text in deploy_text.items():
    if ":" + "latest" in text.lower():
        raise SystemExit(f"FAIL: {name} references a mutable latest image tag")
    if "github.sha" not in text and "GITHUB_SHA" not in text:
        raise SystemExit(f"FAIL: {name} does not derive its artifact from the triggering commit")

direct = deploy_text["deploy-to-jeeb.yml"]
for token in (
    'steps.immutable.outputs.image',
    'GITHUB_OUTPUT',
    'sha256:',
    'scripts/verify-swarm-service-image.sh',
):
    if token not in direct:
        raise SystemExit(f"FAIL: direct production deploy lacks commit/image/runtime proof: {token}")

staging = deploy_text["jeeb-staging-deploy.yml"]
for token in (
    "${{ github.sha }}",
    "steps.immutable.outputs.image",
    "GITHUB_OUTPUT",
    "sha256:",
    "scripts/verify-swarm-service-image.sh",
):
    if token not in staging:
        raise SystemExit(f"FAIL: staging deploy lacks commit/image/runtime proof: {token}")

build = (workflow_dir / "build.yml").read_text()
if ":" + "latest" in build.lower() or "type=raw,value=" + "latest" in build:
    raise SystemExit("FAIL: build publishes a mutable latest deployment artifact")
if "${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:${{ github.sha }}" not in build:
    raise SystemExit("FAIL: build artifact is not tagged with the exact triggering commit")
if re.search(r"(?m)^\s+image_tag:\s*", build):
    raise SystemExit("FAIL: build exposes an arbitrary artifact selector")
if "jeeb-infrastructure/.github/workflows/swarm-deploy.yml" in build:
    raise SystemExit("FAIL: build workflow reintroduced the retired tag-resolving deployer")

if (workflow_dir / "db-backup-verify.yml").exists():
    raise SystemExit("FAIL: retired gateway database restore workflow is active")

if "new service create failed; leaving the failed service in place for inspection" not in lifecycle:
    raise SystemExit("FAIL: failed-create lifecycle no longer fails closed in place")

production = json.loads(Path("src/JeebGateway/appsettings.Production.json").read_text())
flags = production["FeatureFlags"]
expected = {
    "FeatureFlags.UseUpstream.Delivery": flags["UseUpstream"]["Delivery"] is True,
    "FeatureFlags.UseUpstream.Ratings": flags["UseUpstream"]["Ratings"] is True,
    "FeatureFlags.Heartbeat.Enabled": flags["Heartbeat"]["Enabled"] is False,
    "FeatureFlags.AdminAuditMode": flags["AdminAuditMode"] == "dual-write-local-read",
}
bad = [name for name, valid in expected.items() if not valid]
if bad:
    raise SystemExit("FAIL: production authority lock drifted: " + ", ".join(bad))

workflow = direct
for forbidden_control in (
    "durable_" + "requests:",
    "inputs.durable_" + "requests",
    "useupstream" + "delivery)",
    "heart" + "beat)",
):
    if forbidden_control in workflow.lower():
        raise SystemExit(f"FAIL: production workflow exposes backward authority: {forbidden_control}")

for forbidden_control in (
    "GatewayDirect" + "PushDispatchOptions",
    "GatewayDirect" + "Dispatch",
    "Matching" + "Mirror",
    "TopicFallback" + "WhenEmpty",
    "Notifications:NewRequestFanout:" + "Enabled",
):
    if forbidden_control in "\n".join(text for _, text in tracked):
        raise SystemExit(f"FAIL: removed authority/control was reintroduced: {forbidden_control}")

required_counts = {
    "FeatureFlags__DurableRequests__Enabled='false'": 2,
    "FeatureFlags__Heartbeat__Enabled='false'": 2,
    "FeatureFlags__UseUpstream__Delivery='true'": 2,
    "FeatureFlags__UseUpstream__Ratings='true'": 2,
}
for token, count in required_counts.items():
    actual = workflow.count(token)
    if actual != count:
        raise SystemExit(
            f"FAIL: production workflow lock {token!r} occurs {actual} times; expected {count}"
        )

staging_authority = Path(".github/workflows/jeeb-staging-deploy.yml").read_text()
if "add_env FeatureFlags__DurableRequests__Enabled true" not in staging_authority:
    raise SystemExit("FAIL: staging DurableRequests forward authority lock drifted")

program = Path("src/JeebGateway/Program.cs").read_text()
for guard in (
    "AdminAuditMode cannot move behind the settled production rung",
    "Production authority is settled: FeatureFlags:UseUpstream:Delivery",
):
    if guard not in program:
        raise SystemExit(f"FAIL: production startup guard missing: {guard}")

print(f"Audited {utf8_count} tracked UTF-8 files and {len(deploy_inventory)} deploy workflows.")
print("Production/staging authority and immutable artifact locks are exact.")
PY

echo "Forward-only authority audit PASSED"
