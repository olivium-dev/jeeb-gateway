#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

python3 - <<'PY'
import copy
import json
import re
import subprocess
from pathlib import Path

repo_root = Path.cwd()


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
    "service rollback": re.compile(
        r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+service\s+" + r"rollback\b", re.I
    ),
    "stack deletion": re.compile(
        r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)\s+stack\s+" + r"rm\b", re.I
    ),
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


def normalized_shell_source(source):
    """Collapse shell line continuations before inspecting commands."""
    return re.sub(r"\\[ \t]*\r?\n[ \t]*", " ", source)


def is_required_staging_failure_safety(path, label, line):
    """Allow only the reviewed ingress-safe Swarm rollback configuration."""
    if path != Path(".github/workflows/jeeb-staging-deploy.yml"):
        return False
    stripped = line.strip()
    allowed = {
        "automatic rollback": {
            "--update-failure-action " + "rollback",
        },
        "rollback option": {
            "--" + "rollback-order start-first --" + "rollback-parallelism 1 --" + "rollback-monitor 20s",
            "--" + "rollback-failure-action pause",
        },
    }
    return stripped in allowed.get(label, set())


adversarial_canaries = {
    "variable Docker deletion": 'DOCKER=docker; "$DOCKER" service ' + "rm app",
    "variable Git prior selector": "git check" + 'out "$PREVIOUS_SHA"',
    "mutable Swarm image": (
        "docker service update --image ghcr.io/olivium-dev/app:" + "production app"
    ),
    "unsafe service scale": "docker service " + "scale app=0",
    "variable Docker rollback": 'ENGINE=docker; "$ENGINE" service ' + "rollback app",
    "variable Docker stack deletion": 'ENGINE=docker; "$ENGINE" stack ' + "rm app",
    "multiline Docker rollback": "docker service " + "\\  \n" + "rollback app",
    "multiline variable Docker deletion": (
        'ENGINE=docker; "$ENGINE" service ' + "\\  \n" + "rm app"
    ),
}
for description, canary in adversarial_canaries.items():
    normalized_canary = normalized_shell_source(canary)
    if not any(pattern.search(normalized_canary) for pattern in forbidden.values()):
        raise SystemExit(f"FAIL: scanner does not reject adversarial canary: {description}")

violations = []
utf8_count = 0
tracked = list(tracked_utf8())
for path, text in tracked:
    if path.name == "Dockerfile" or path.name.startswith("Dockerfile."):
        local_stages = set()
        stage_count = 0
        for line in text.splitlines():
            match = re.match(
                r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?\s*$",
                line,
                re.I,
            )
            if not match:
                continue
            stage_count += 1
            image, alias = match.groups()
            if image.lower() not in local_stages and not re.search(r"@sha256:[0-9a-f]{64}$", image, re.I):
                raise SystemExit(f"FAIL: unpinned Dockerfile stage in {path}: {line.strip()}")
            if alias:
                local_stages.add(alias.lower())
        if stage_count == 0:
            raise SystemExit(f"FAIL: no Dockerfile stages audited in {path}")

    if "cloudflared-linux-amd64.deb" in text and ("curl " in text or "wget " in text):
        if "/releases/" + "latest/" in text:
            raise SystemExit(f"FAIL: floating cloudflared download in {path}")
        for marker in (
            "/releases/download/2026.8.2/cloudflared-linux-amd64.deb",
            "c805c7c8102190c04dfc16e3b4cc4acc9007d5b19b3afbcd608ea6fed7645a43",
            "sha256sum --check --strict",
        ):
            if marker not in text:
                raise SystemExit(f"FAIL: cloudflared download lacks exact artifact proof in {path}: {marker}")

for path, text in tracked:
    utf8_count += 1
    normalized_text = normalized_shell_source(text)
    for line_number, line in enumerate(normalized_text.splitlines(), 1):
        for label, pattern in forbidden.items():
            if (
                label == "Git prior-state recovery"
                and path == Path("tests/gw3-pack/neg-controls.sh")
                and line.strip().startswith("u_N17()")
            ):
                continue
            if is_required_staging_failure_safety(path, label, line):
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

engine_pattern = r"(?:\bdocker\b|[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?)"
mutation_pattern = re.compile(
    engine_pattern + r"\s+(?:service\s+(?:update|create|scale|" + "rm|rollback" + r")"
    r"|stack\s+(?:create|update|deploy|" + "rm" + r"))\b", re.I
)
engine_api_mutation_pattern = re.compile(
    r"/services/[A-Za-z0-9_.$\{\}-]+/update\?version=", re.I
)
for canary in (
    "docker service " + "\\  \n" + 'update --image "$IMAGE" app',
    'ENGINE=docker\n"$ENGINE" service ' + 'update --image "$IMAGE" app',
    "docker service " + "scale app=0",
    'ENGINE=docker\n"$ENGINE" service ' + "rollback app",
    'ENGINE=docker\n"$ENGINE" stack ' + 'deploy -c stack.yml app',
    'ENGINE=docker\n"$ENGINE" service ' + "\\  \n" + "rollback app",
    "curl --unix-socket /var/run/docker.sock "
    "http://localhost/v1.51/services/$service_id/update?version=$version",
):
    normalized_canary = normalized_shell_source(canary)
    if not (
        mutation_pattern.search(normalized_canary)
        or engine_api_mutation_pattern.search(normalized_canary)
    ):
        raise SystemExit("FAIL: service mutation inventory misses an adversarial canary")
mutation_inventory = {
    path.relative_to(Path(".")).as_posix()
    for root in (Path(".github/workflows"), Path(".github/scripts"))
    for path in root.rglob("*")
    if path.is_file() and path.suffix in {".yml", ".yaml", ".sh"}
    and (
        mutation_pattern.search(normalized_shell_source(path.read_text()))
        or engine_api_mutation_pattern.search(normalized_shell_source(path.read_text()))
    )
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


OWNER_STEP_NAME = "Owner block - forward-only promotion pending"
OWNER_ERROR = (
    "::error::Forward-only promotion pending owner-approved failure handling; "
    "no image, SSH, provider, secret, or Swarm mutation was attempted."
)
OWNER_RUN_LINES = (f"echo '{OWNER_ERROR}' >&2", "exit 1")
STATUS_BYPASS = re.compile(r"\b(?:always|failure|cancelled)\s*\(", re.I)
SUCCESS_STATUS = re.compile(r"\bsuccess\s*\(", re.I)
MUTATION_STEP_MARKERS = (
    "docker/login-action@",
    "docker/build-push-action@",
    "actions/upload-artifact@",
    "docker login",
    "docker build",
    "docker service update",
    "/services/$service_id/update?version=",
    "docker secret create",
    "ssh jeeb",
)
EXPECTED_AUTHORITY_JOBS = {
    "deploy-to-jeeb.yml": "deploy",
    "jeeb-staging-deploy.yml": "deploy",
    "jeeb-staging-state-auth-smoke.yml": "smoke",
}


def load_workflow(path):
    ruby = r'''
require "json"
require "yaml"
document = YAML.safe_load(File.read(ARGV.fetch(0)), aliases: true)
raise "workflow root must be a mapping" unless document.is_a?(Hash)
STDOUT.write(JSON.generate(document))
'''
    output = subprocess.check_output(
        ["ruby", "-rjson", "-ryaml", "-e", ruby, str(path)],
        text=True,
    )
    return json.loads(output)


def run_owner_body(body):
    return subprocess.run(
        ["/bin/bash", "--noprofile", "--norc", "-c", body],
        cwd=repo_root,
        env={"PATH": ""},
        text=True,
        capture_output=True,
        check=False,
    )


def reject_bypass(name, node):
    if "continue-on-error" in node:
        raise ValueError(f"{name} declares continue-on-error")
    condition = str(node.get("if", ""))
    if STATUS_BYPASS.search(condition):
        raise ValueError(f"{name} declares a terminal-status bypass: {condition}")
    if SUCCESS_STATUS.search(condition):
        normalized = re.sub(r"\s+", "", condition).lower()
        if normalized not in {"success()", "${{success()}}"}:
            raise ValueError(f"{name} has a non-canonical success condition: {condition}")


def validate_workflow_authority(name, document):
    if "defaults" in document:
        raise ValueError(f"{name} overrides the canonical workflow shell")
    jobs = document.get("jobs")
    if not isinstance(jobs, dict) or not jobs:
        raise ValueError(f"{name} has no structurally parsed jobs")
    expected_job = EXPECTED_AUTHORITY_JOBS.get(name)
    if expected_job is None or set(jobs) != {expected_job}:
        raise ValueError(
            f"{name} job authority drifted: actual={sorted(jobs)} expected={[expected_job]}"
        )

    for job_name, job in jobs.items():
        if not isinstance(job, dict):
            raise ValueError(f"{name}:{job_name} is not a job mapping")
        reject_bypass(f"{name}:{job_name}", job)
        if "defaults" in job:
            raise ValueError(f"{name}:{job_name} overrides the canonical job shell")
        steps = job.get("steps")
        if not isinstance(steps, list) or not steps:
            raise ValueError(f"{name}:{job_name} has no structurally parsed steps")
        owner = steps[0]
        if not isinstance(owner, dict) or set(owner) != {"name", "run"}:
            raise ValueError(f"{name}:{job_name} first executable step is not canonical")
        if owner.get("name") != OWNER_STEP_NAME:
            raise ValueError(f"{name}:{job_name} owner step is not first")
        body = owner.get("run")
        if not isinstance(body, str) or tuple(body.splitlines()) != OWNER_RUN_LINES:
            raise ValueError(f"{name}:{job_name} owner run body is not canonical")
        for index, step in enumerate(steps):
            if not isinstance(step, dict):
                raise ValueError(f"{name}:{job_name}:step-{index} is not a mapping")
            reject_bypass(f"{name}:{job_name}:step-{index}", step)
        result = run_owner_body(body)
        if result.returncode != 1 or OWNER_ERROR not in result.stderr or result.stdout:
            raise ValueError(f"{name}:{job_name} owner run body does not fail loudly under empty PATH")


def find_later_mutation_step(document):
    for job in document["jobs"].values():
        for step in job["steps"][1:]:
            surface = json.dumps(step, sort_keys=True)
            if any(marker in surface for marker in MUTATION_STEP_MARKERS):
                return step
    raise ValueError("workflow has no later mutation step for the adversarial control")


def assert_workflow_rejected(description, name, document):
    try:
        validate_workflow_authority(name, document)
    except ValueError:
        return
    raise SystemExit(f"FAIL: {name} unsafe owner-block mutation survived: {description}")


def validate_lifecycle_execution(source=None):
    if source is None:
        command = [
            "/bin/bash",
            "--noprofile",
            "--norc",
            "-x",
            str(Path(".github/scripts/jeeb-gateway-secret-lifecycle.sh")),
            "gc",
            "jeeb-gateway",
        ]
        input_text = None
    else:
        command = [
            "/bin/bash",
            "--noprofile",
            "--norc",
            "-x",
            "-s",
            "--",
            "gc",
            "jeeb-gateway",
        ]
        input_text = source
    result = subprocess.run(
        command,
        cwd=repo_root,
        env={"PATH": ""},
        input=input_text,
        text=True,
        capture_output=True,
        check=False,
    )
    trace = result.stderr
    if result.returncode != 1 or OWNER_ERROR not in trace:
        raise ValueError("secret lifecycle does not fail loudly with exit 1 under empty PATH")
    if "+ exit 1" not in trace or re.search(r"(?m)^\+ docker\b", trace):
        raise ValueError("secret lifecycle can reach Docker before the owner exit")


deploy_text = {name: (workflow_dir / name).read_text() for name in expected_inventory}
for name, text in deploy_text.items():
    if ":" + "latest" in text.lower():
        raise SystemExit(f"FAIL: {name} references a mutable latest image tag")
    if "github.sha" not in text and "GITHUB_SHA" not in text:
        raise SystemExit(f"FAIL: {name} does not derive its artifact from the triggering commit")

workflow_authority_paths = {
    "deploy-to-jeeb.yml": workflow_dir / "deploy-to-jeeb.yml",
    "jeeb-staging-deploy.yml": workflow_dir / "jeeb-staging-deploy.yml",
    "jeeb-staging-state-auth-smoke.yml": workflow_dir / "jeeb-staging-state-auth-smoke.yml",
}
if len(workflow_authority_paths) + 1 != len(expected_mutation_inventory):
    raise SystemExit("FAIL: owner-block authority inventory is incomplete")
for name, path in workflow_authority_paths.items():
    document = load_workflow(path)
    try:
        validate_workflow_authority(name, document)
    except ValueError as error:
        raise SystemExit(f"FAIL: {error}") from error

    wrapped_exit = copy.deepcopy(document)
    wrapped_exit["jobs"][next(iter(wrapped_exit["jobs"]))]["steps"][0]["run"] = (
        f"echo '{OWNER_ERROR}' >&2\nif false; then\n  exit 1\nfi\n"
    )
    assert_workflow_rejected("exit wrapped in if false", name, wrapped_exit)

    continued_owner = copy.deepcopy(document)
    continued_owner["jobs"][next(iter(continued_owner["jobs"]))]["steps"][0][
        "continue-on-error"
    ] = True
    assert_workflow_rejected("owner continue-on-error", name, continued_owner)

    terminal_bypass = copy.deepcopy(document)
    find_later_mutation_step(terminal_bypass)["if"] = "${{ always() }}"
    assert_workflow_rejected("later mutation always() bypass", name, terminal_bypass)

    commented_owner = copy.deepcopy(document)
    commented_owner["jobs"][next(iter(commented_owner["jobs"]))]["steps"][0]["run"] = (
        f"# {OWNER_STEP_NAME}\n# echo '{OWNER_ERROR}' >&2\n# exit 1\ntrue\n"
    )
    assert_workflow_rejected("owner body moved to comments", name, commented_owner)

    cross_job_bypass = copy.deepcopy(document)
    primary_job = next(iter(cross_job_bypass["jobs"]))
    cross_job_bypass["jobs"]["bypass"] = {
        "needs": primary_job,
        "if": "${{ always() }}",
        "runs-on": "ubuntu-22.04",
        "steps": [{"run": "docker service update --force jeeb-gateway"}],
    }
    assert_workflow_rejected(
        "second-job terminal-status mutation bypass", name, cross_job_bypass
    )

try:
    validate_lifecycle_execution()
except ValueError as error:
    raise SystemExit(f"FAIL: {error}") from error
for description, mutated in (
    (
        "lifecycle exit wrapped in if false",
        lifecycle.replace("exit 1", "if false; then\n  exit 1\nfi", 1),
    ),
    (
        "lifecycle owner body moved to comments",
        lifecycle.replace(
            f"echo '{OWNER_ERROR}' >&2\nexit 1",
            f"# echo '{OWNER_ERROR}' >&2\n# exit 1\ntrue",
            1,
        ),
    ),
):
    try:
        validate_lifecycle_execution(mutated)
    except ValueError:
        continue
    raise SystemExit(f"FAIL: unsafe secret lifecycle mutation survived: {description}")

print(
    "Structurally validated 3 single-job workflow authorities, dynamically executed "
    "their owner steps under empty PATH, and rejected 15 adversarial workflow mutations."
)
print("Dynamically validated the blocked lifecycle authority and 2 adversarial mutations.")

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
    'add_env ForwardedHeaders__KnownProxies__0 "$published_port_proxy_ip"',
    "add_env Gateway__PublicBaseUrl https://app.jeeb.fds-1.com",
    "add_env AdminPortal__AllowedOrigins__0 https://app.jeeb.fds-1.com",
    "add_env AdminPortal__AllowedOrigins__1 https://cms.jeeb.fds-1.com",
    "scripts/probe-staging-public-gateway-contract.sh",
    "add_env Security__TokenMint__Enabled true",
    "source scripts/staging-gateway-mutation-lock.sh",
    "staging_gateway_lock_acquire",
    "staging_gateway_lock_assert",
    "staging_gateway_lock_release",
):
    if token not in staging:
        raise SystemExit(f"FAIL: staging deploy lacks commit/image/runtime proof: {token}")
if "ForwardedHeaders__KnownNetworks" in staging:
    raise SystemExit("FAIL: staging deploy trusts a forwarded-header network range")

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
    "FeatureFlags__DurableRequests__Enabled='false'": 1,
    "FeatureFlags__Heartbeat__Enabled='false'": 1,
    "FeatureFlags__UseUpstream__Delivery='true'": 1,
    "FeatureFlags__UseUpstream__Ratings='true'": 1,
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
print("Production/staging authority, immutable artifacts, and loud owner blocks are exact.")
PY

bash scripts/test-reject-staging-gateway-alias.sh
echo "Forward-only authority audit PASSED"
