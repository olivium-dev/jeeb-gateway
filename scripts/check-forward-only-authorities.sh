#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

python3 - <<'PY'
import copy
import hashlib
import json
import re
import shlex
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
    """No deployment path may exempt automatic or manual rollback controls."""
    return False


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
    "jeeb-production-deploy.yml",
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
    ".github/scripts/rotate-staging-gateway-probe-key.sh",
    ".github/workflows/deploy-to-jeeb.yml",
    ".github/workflows/jeeb-production-deploy.yml",
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
SECURITY_CUTOVER_OWNER_IF = "${{ inputs.deployment_mode == 'normal' }}"
SECURITY_CUTOVER_INPUT = {
    "description": "Protected staging deployment mode",
    "required": True,
    "default": "normal",
    "type": "choice",
    "options": ["normal", "security-cutover", "otp-cutover", "devtool-reassert"],
}
PROVIDER_EXPAND_VERIFIED_INPUT = {
    "description": "Confirm protected-main push-notification is live and verified in expand mode",
    "required": True,
    "type": "boolean",
    "default": False,
}
STAGING_DISPATCH_INPUTS = {
    "deployment_mode": SECURITY_CUTOVER_INPUT,
    "provider_expand_verified": PROVIDER_EXPAND_VERIFIED_INPUT,
}
PROVIDER_EXPAND_HOLD_NAME = "Hold caller activation until relay expand is verified"
PROVIDER_EXPAND_HOLD_IF = (
    "${{ inputs.deployment_mode != 'normal' && inputs.provider_expand_verified != true }}"
)
PROVIDER_EXPAND_HOLD_LINES = (
    "echo '::error::Deployment HOLD: deploy and verify the protected-main push-notification image in expand mode first.' >&2",
    "exit 1",
)
SECURITY_CUTOVER_GATE_NAME = "Require designated staging owner"
SECURITY_CUTOVER_GATE_IF = "${{ inputs.deployment_mode != 'normal' }}"
SECURITY_CUTOVER_GATE_ENV = {
    "REQUESTING_ACTOR": "${{ github.actor }}",
    "TRIGGERING_ACTOR": "${{ github.triggering_actor }}",
}
SECURITY_CUTOVER_GATE_LINES = (
    "set -euo pipefail",
    'if [ "$REQUESTING_ACTOR" != oudaykhaled ] \\',
    '  || [ "$TRIGGERING_ACTOR" != oudaykhaled ]; then',
    "  echo '::error::Protected staging deployments require the designated owner.' >&2",
    "  exit 1",
    "fi",
)
PRODUCTION_WORKFLOW_NAME = "jeeb-production-deploy.yml"
PRODUCTION_CONFIRMATION_INPUT = {
    "description": "Type deploy-jeeb-production",
    "required": True,
    "type": "string",
}
PRODUCTION_GATE_NAME = "Require protected production source and owner confirmation"
PRODUCTION_GATE_ENV = {
    "CONFIRM_PRODUCTION": "${{ inputs.confirm_production }}",
    "DEFAULT_BRANCH": "${{ github.event.repository.default_branch }}",
    "GITHUB_REF_PROTECTED": "${{ github.ref_protected }}",
    "REQUESTING_ACTOR": "${{ github.actor }}",
    "TRIGGERING_ACTOR": "${{ github.triggering_actor }}",
}
PRODUCTION_GATE_LINES = (
    "set -euo pipefail",
    '[ "$GITHUB_REPOSITORY" = olivium-dev/jeeb-gateway ]',
    '[ "$CONFIRM_PRODUCTION" = deploy-jeeb-production ] || {',
    "  echo '::error::Production confirmation is incorrect.' >&2",
    "  exit 64",
    "}",
    '[ "$REQUESTING_ACTOR" = oudaykhaled ] \\',
    '  && [ "$TRIGGERING_ACTOR" = oudaykhaled ] || {',
    "    echo '::error::Production deploys require the designated owner.' >&2",
    "    exit 65",
    "  }",
    ': "${DEFAULT_BRANCH:?repository default branch is required}"',
    '[ "$GITHUB_REF" = "refs/heads/${DEFAULT_BRANCH}" ] || {',
    "  echo '::error::Production deploys may run only from the default branch.' >&2",
    "  exit 66",
    "}",
    '[ "$GITHUB_REF_PROTECTED" = true ] || {',
    "  echo '::error::The production source branch must be protected.' >&2",
    "  exit 67",
    "}",
)
STATUS_BYPASS = re.compile(r"\b(?:always|failure|cancelled)\s*\(", re.I)
SUCCESS_STATUS = re.compile(r"\bsuccess\s*\(", re.I)
CREDENTIAL_CLEANUP_CONDITION = (
    "${{ always() && steps.remote_ghcr_login.outcome != 'skipped' }}"
)
CREDENTIAL_CLEANUP_LINES = (
    "set -euo pipefail",
    "# The run-scoped credential path is intentionally expanded by the runner.",
    "# shellcheck disable=SC2029",
    'ssh jeeb-staging "set -eu',
    '  credential_dir=\\"\\$HOME/$REMOTE_DOCKER_CONFIG\\"',
    '  case \\"\\$credential_dir\\" in \\"\\$HOME/.jeeb-deploy/ghcr-${{ github.run_id }}-${{ github.run_attempt }}\\") ;; *) exit 97 ;; esac',
    '  [ ! -L \\"\\$credential_dir\\" ] || exit 98',
    '  [ ! -e \\"\\$credential_dir\\" ] || [ -d \\"\\$credential_dir\\" ] || exit 98',
    '  if [ -d \\"\\$credential_dir\\" ]; then',
    '    DOCKER_CONFIG=\\"\\$credential_dir\\" docker logout ghcr.io >/dev/null 2>&1 || true',
    '    rm -f -- \\"\\$credential_dir/config.json\\"',
    '    rmdir -- \\"\\$credential_dir\\"',
    "  fi",
    '  [ ! -e \\"\\$credential_dir\\" ]"',
)
PRODUCTION_CREDENTIAL_CLEANUP_NAME = "Clean up isolated production Docker credentials"
PRODUCTION_CREDENTIAL_CLEANUP_LINES = (
    "set -euo pipefail",
    "# The run-scoped credential path is intentionally expanded by the runner.",
    "# shellcheck disable=SC2029",
    'ssh jeeb-production "set -eu',
    '  [ \\"\\$(readlink -f -- \\"\\$HOME\\")\\" = \\"\\$HOME\\" ] || exit 98',
    '  deploy_root=\\"\\$HOME/.jeeb-deploy\\"',
    '  credential_dir=\\"\\$HOME/$REMOTE_DOCKER_CONFIG\\"',
    '  case \\"\\$credential_dir\\" in \\"\\$HOME/.jeeb-deploy/ghcr-${{ github.run_id }}-${{ github.run_attempt }}\\") ;; *) exit 97 ;; esac',
    '  [ ! -L \\"\\$deploy_root\\" ] && [ -d \\"\\$deploy_root\\" ] || exit 98',
    '  [ ! -L \\"\\$credential_dir\\" ] || exit 98',
    '  [ ! -e \\"\\$credential_dir\\" ] || [ -d \\"\\$credential_dir\\" ] || exit 98',
    '  [ \\"\\$(readlink -f -- \\"\\$deploy_root\\")\\" = \\"\\$deploy_root\\" ] || exit 98',
    '  [ \\"\\$(stat -c \'%u:%a\' -- \\"\\$deploy_root\\")\\" = \\"\\$(id -u):700\\" ] || exit 98',
    '  if [ -d \\"\\$credential_dir\\" ]; then',
    '    [ \\"\\$(readlink -f -- \\"\\$credential_dir\\")\\" = \\"\\$credential_dir\\" ] || exit 98',
    '    [ \\"\\$(stat -c \'%u:%a\' -- \\"\\$credential_dir\\")\\" = \\"\\$(id -u):700\\" ] || exit 98',
    '    DOCKER_CONFIG=\\"\\$credential_dir\\" docker logout ghcr.io >/dev/null 2>&1 || true',
    '    rm -f -- \\"\\$credential_dir/config.json\\"',
    '    rmdir -- \\"\\$credential_dir\\"',
    "  fi",
    '  [ ! -e \\"\\$credential_dir\\" ]"',
)
PRODUCTION_EXACT_SHA_CHECKS = (
    "audit",
    "build",
    "build-and-test",
    "Gitleaks Secret Scan",
    "gwdbx-flag-registry-gate",
    "nswag-freshness",
    "nswag-otp-freshness",
    "provider-boundary-gates",
    "stateless-gate",
    "docker",
)
PRODUCTION_CI_GATE_LINES = (
    "set -euo pipefail",
    "checks=$(gh api \\",
    "  -H 'Accept: application/vnd.github+json' \\",
    '  "/repos/$GITHUB_REPOSITORY/commits/$GITHUB_SHA/check-runs?per_page=100")',
    "for required in \\",
    "  audit \\",
    "  build \\",
    "  build-and-test \\",
    "  'Gitleaks Secret Scan' \\",
    "  gwdbx-flag-registry-gate \\",
    "  nswag-freshness \\",
    "  nswag-otp-freshness \\",
    "  provider-boundary-gates \\",
    "  stateless-gate \\",
    "  docker; do",
    '  state=$(jq -r --arg name "$required" \'',
    "    [.check_runs[] | select(.name == $name)]",
    "    | sort_by(.started_at)",
    "    | last",
    '    | "\\(.status):\\(.conclusion)"',
    '  \' <<<"$checks")',
    '  [ "$state" = completed:success ] || {',
    '    echo "::error::Exact-SHA check \'$required\' is not successful ($state)." >&2',
    "    exit 69",
    "  }",
    "done",
)
PRODUCTION_HEALTH_COMMAND_LINES = (
    "set -eu",
    "wget --no-verbose --tries=1 --spider http://localhost:8080/health/ready || exit 1",
)
PRODUCTION_REMOTE_LOGIN_BODY_SHA256 = "7728bc5eb7225a3c2763109f2920c2d88803278f2772d8d67a0ce623a97e6f26"
PRODUCTION_UPDATE_BODY_SHA256 = "e11c28f46c791518e51cb2089d7cfee459573f4c9932424048d5d63d0d6df012"
PRODUCTION_RELAY_HOLD_NAME = "Hold production activation pending scoped relay preflight"
PRODUCTION_RELAY_HOLD_LINES = (
    "echo '::error::Production activation is held until the scoped relay key mount and authenticated provider-expand preflight are implemented.' >&2",
    "exit 1",
)
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
    "jeeb-production-deploy.yml": "deploy",
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


def run_security_cutover_gate(body, actor, triggering_actor):
    return subprocess.run(
        ["/bin/bash", "--noprofile", "--norc", "-c", body],
        cwd=repo_root,
        env={
            "PATH": "",
            "REQUESTING_ACTOR": actor,
            "TRIGGERING_ACTOR": triggering_actor,
        },
        text=True,
        capture_output=True,
        check=False,
    )


def run_production_gate(body, **overrides):
    environment = {
        "PATH": "",
        "CONFIRM_PRODUCTION": "deploy-jeeb-production",
        "DEFAULT_BRANCH": "main",
        "GITHUB_REF": "refs/heads/main",
        "GITHUB_REF_PROTECTED": "true",
        "GITHUB_REPOSITORY": "olivium-dev/jeeb-gateway",
        "REQUESTING_ACTOR": "oudaykhaled",
        "TRIGGERING_ACTOR": "oudaykhaled",
    }
    environment.update(overrides)
    return subprocess.run(
        ["/bin/bash", "--noprofile", "--norc", "-c", body],
        cwd=repo_root,
        env=environment,
        text=True,
        capture_output=True,
        check=False,
    )


def reject_bypass(name, node):
    if "continue-on-error" in node:
        raise ValueError(f"{name} declares continue-on-error")
    condition = str(node.get("if", ""))
    allowed_failure_cleanup = condition == CREDENTIAL_CLEANUP_CONDITION and (
        (
            name.startswith("jeeb-staging-deploy.yml:deploy:step-")
            and node.get("name") == "Clean up isolated remote Docker credentials"
            and tuple(str(node.get("run", "")).splitlines())
            == CREDENTIAL_CLEANUP_LINES
        )
        or (
            name.startswith(f"{PRODUCTION_WORKFLOW_NAME}:deploy:step-")
            and node.get("name") == PRODUCTION_CREDENTIAL_CLEANUP_NAME
            and tuple(str(node.get("run", "")).splitlines())
            == PRODUCTION_CREDENTIAL_CLEANUP_LINES
        )
    )
    if STATUS_BYPASS.search(condition) and not allowed_failure_cleanup:
        raise ValueError(f"{name} declares a terminal-status bypass: {condition}")
    if SUCCESS_STATUS.search(condition):
        normalized = re.sub(r"\s+", "", condition).lower()
        if normalized not in {"success()", "${{success()}}"}:
            raise ValueError(f"{name} has a non-canonical success condition: {condition}")


def executable_lines(source):
    return tuple(
        line.strip()
        for line in source.splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    )


def exact_sha_check_names(source):
    collecting = False
    chunks = []
    for line in source.splitlines():
        stripped = line.strip()
        if stripped == "for required in \\":
            collecting = True
            continue
        if not collecting:
            continue
        terminal = stripped.endswith("; do")
        chunk = stripped[:-4].rstrip() if terminal else stripped
        if chunk.endswith("\\"):
            chunk = chunk[:-1].rstrip()
        if chunk:
            chunks.append(chunk)
        if terminal:
            break
    if not collecting or not chunks:
        raise ValueError("production exact-SHA check loop is missing")
    return tuple(shlex.split(" ".join(chunks)))


def production_health_command(source):
    match = re.search(
        r"(?ms)^health_cmd=\$\(cat <<'HEALTH'\n(?P<body>.*?)\nHEALTH\n\)",
        source,
    )
    if match is None:
        raise ValueError("production health command block is missing")
    return tuple(line.strip() for line in match.group("body").splitlines())


def validate_production_authority(document, job_name, job, steps):
    dispatch = document.get("on")
    expected_dispatch = {
        "workflow_dispatch": {
            "inputs": {"confirm_production": PRODUCTION_CONFIRMATION_INPUT}
        }
    }
    if dispatch != expected_dispatch:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} dispatch contract drifted")
    if job.get("environment") != "production":
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} environment drifted")
    expected_permissions = {"checks": "read", "contents": "read", "packages": "write"}
    if document.get("permissions") != expected_permissions:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} permissions drifted")

    gate = steps[0]
    if not isinstance(gate, dict) or set(gate) != {"name", "env", "run"}:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} gate shape drifted")
    if gate.get("name") != PRODUCTION_GATE_NAME or gate.get("env") != PRODUCTION_GATE_ENV:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} gate identity drifted")
    body = gate.get("run")
    if not isinstance(body, str) or tuple(body.splitlines()) != PRODUCTION_GATE_LINES:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} gate body drifted")

    accepted = run_production_gate(body)
    if accepted.returncode != 0 or accepted.stdout or accepted.stderr:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} valid owner gate was rejected")
    rejected_cases = (
        ({"GITHUB_REPOSITORY": "another/repository"}, 1),
        ({"CONFIRM_PRODUCTION": "wrong"}, 64),
        ({"REQUESTING_ACTOR": "another-actor"}, 65),
        ({"TRIGGERING_ACTOR": "rerun-by-another-actor"}, 65),
        ({"DEFAULT_BRANCH": "release"}, 66),
        ({"GITHUB_REF": "refs/heads/feature"}, 66),
        ({"GITHUB_REF_PROTECTED": "false"}, 67),
    )
    for overrides, expected_code in rejected_cases:
        rejected = run_production_gate(body, **overrides)
        if rejected.returncode != expected_code or rejected.stdout:
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} gate predicate is not fail-closed"
            )

    if len(steps) < 3 or set(steps[1]) != {"name", "run"}:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} relay activation hold drifted")
    if steps[1].get("name") != PRODUCTION_RELAY_HOLD_NAME or tuple(
        str(steps[1].get("run", "")).splitlines()
    ) != PRODUCTION_RELAY_HOLD_LINES:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} relay activation hold drifted")
    if set(steps[2]) != {"uses"} or not str(
        steps[2]["uses"]
    ).startswith("actions/checkout@"):
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} checkout is not after the gate")
    for index, step in enumerate(steps):
        if not isinstance(step, dict):
            raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name}:step-{index} is not a mapping")
        reject_bypass(f"{PRODUCTION_WORKFLOW_NAME}:{job_name}:step-{index}", step)
    first_mutation = next(
        (
            index
            for index, step in enumerate(steps)
            if any(marker in json.dumps(step, sort_keys=True) for marker in MUTATION_STEP_MARKERS)
        ),
        None,
    )
    if first_mutation is None or first_mutation <= 0:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} mutation precedes the gate")

    ci_indices = [
        index
        for index, step in enumerate(steps)
        if step.get("name") == "Require successful exact-SHA CI"
    ]
    if ci_indices != [4] or first_mutation <= ci_indices[0]:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} exact-SHA CI gate drifted")
    ci_gate = steps[ci_indices[0]]
    if set(ci_gate) != {"name", "env", "run"} or ci_gate.get("env") != {
        "GH_TOKEN": "${{ secrets.GITHUB_TOKEN }}"
    }:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} exact-SHA CI gate shape drifted")
    ci_body = ci_gate.get("run")
    if (
        not isinstance(ci_body, str)
        or tuple(ci_body.splitlines()) != PRODUCTION_CI_GATE_LINES
        or exact_sha_check_names(ci_body) != PRODUCTION_EXACT_SHA_CHECKS
    ):
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} exact-SHA check set drifted")

    update_steps = [
        step
        for step in steps
        if step.get("name") == "Update production gateway and verify production posture"
    ]
    if len(update_steps) != 1 or set(update_steps[0]) != {"name", "env", "run"}:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} deploy step shape drifted")
    update_source = update_steps[0].get("run")
    if not isinstance(update_source, str):
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} deploy body is missing")
    if hashlib.sha256(update_source.encode()).hexdigest() != PRODUCTION_UPDATE_BODY_SHA256:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} executable deploy body drifted")
    if production_health_command(update_source) != PRODUCTION_HEALTH_COMMAND_LINES:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} process-only health command drifted")
    dockerfile = Path("Dockerfile").read_text()
    if "CMD " + PRODUCTION_HEALTH_COMMAND_LINES[1] not in normalized_shell_source(dockerfile):
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} health command diverges from image")

    active_update_lines = executable_lines(update_source)
    required_env = (
        "ASPNETCORE_ENVIRONMENT=Production",
        "DOTNET_ENVIRONMENT=Production",
        "SuperLogin__OpenMode=false",
        "DemoUsers__Enabled=false",
        "Features__DevEndpoints__Enabled=false",
        "Features__Swagger__Enabled=false",
        "Security__TokenMint__Enabled=true",
        "TestControlPlane__Enabled=false",
    )
    actual_env = tuple(
        line.removeprefix("--env-add ").removesuffix(" \\")
        for line in active_update_lines
        if line.startswith("--env-add ")
    )
    if actual_env != required_env:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} production environment set drifted")
    for required_line in (
        "wait_for_readiness",
        "expect_404 /api/User/demo-users",
        "expect_404 /api/User/super-login/users",
        "expect_404 /swagger/v1/swagger.json",
        "expect_404 /swagger/index.html",
        "expect_404 /__test/clock",
        "verify_production_posture",
    ):
        if active_update_lines.count(required_line) != 1:
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} executable posture gate drifted: {required_line}"
            )

    login_steps = [step for step in steps if step.get("name") == "Log remote Docker in to GHCR"]
    if len(login_steps) != 1 or set(login_steps[0]) != {"name", "id", "run"}:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} remote login step shape drifted")
    login_source = str(login_steps[0].get("run", ""))
    if hashlib.sha256(login_source.encode()).hexdigest() != PRODUCTION_REMOTE_LOGIN_BODY_SHA256:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} credential login body drifted")
    login_lines = executable_lines(login_source)
    docker_login_line = next(
        (index for index, line in enumerate(login_lines) if "docker login ghcr.io" in line),
        -1,
    )
    required_login_sequence = (
        '[ \\"\\$(readlink -f -- \\"\\$HOME\\")\\" = \\"\\$HOME\\" ] || exit 98',
        'deploy_root=\\"\\$HOME/.jeeb-deploy\\"',
        'credential_dir=\\"\\$HOME/$REMOTE_DOCKER_CONFIG\\"',
        '[ ! -L \\"\\$deploy_root\\" ] && [ -d \\"\\$deploy_root\\" ] || exit 98',
        '[ ! -L \\"\\$credential_dir\\" ] && [ -d \\"\\$credential_dir\\" ] || exit 98',
        '[ \\"\\$(readlink -f -- \\"\\$deploy_root\\")\\" = \\"\\$deploy_root\\" ] || exit 98',
        '[ \\"\\$(readlink -f -- \\"\\$credential_dir\\")\\" = \\"\\$credential_dir\\" ] || exit 98',
    )
    sequence_position = login_lines.index('| ssh jeeb-production \\')
    for guard in required_login_sequence:
        try:
            sequence_position = login_lines.index(guard, sequence_position + 1)
        except ValueError as error:
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} credential pre-login guard drifted: {guard}"
            ) from error
    if docker_login_line < 0 or sequence_position > docker_login_line:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} credential login precedes validation")
    for setup_line in (
        '[ ! -e \\"\\$credential_dir\\" ] && [ ! -L \\"\\$credential_dir\\" ] || exit 98',
        'mkdir -m 700 -- \\"\\$credential_dir\\"',
    ):
        if login_lines.count(setup_line) != 1 or login_lines.index(setup_line) > login_lines.index(
            '| ssh jeeb-production \\'
        ):
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} secure credential creation drifted: {setup_line}"
            )

    cleanup_indices = [
        index
        for index, step in enumerate(steps)
        if step.get("name") == PRODUCTION_CREDENTIAL_CLEANUP_NAME
    ]
    if cleanup_indices != [len(steps) - 1]:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} cleanup position drifted")
    cleanup = steps[cleanup_indices[0]]
    if set(cleanup) != {"name", "if", "run"}:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} cleanup shape drifted")
    if cleanup.get("if") != CREDENTIAL_CLEANUP_CONDITION:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} cleanup condition drifted")
    if tuple(str(cleanup.get("run", "")).splitlines()) != PRODUCTION_CREDENTIAL_CLEANUP_LINES:
        raise ValueError(f"{PRODUCTION_WORKFLOW_NAME}:{job_name} cleanup body drifted")

    surface = "\n".join(
        line
        for step in steps
        for line in (
            executable_lines(str(step.get("run", "")))
            + ((str(step.get("uses")),) if step.get("uses") else ())
        )
    )
    required_contract = (
        "audit",
        "build",
        "build-and-test",
        "Gitleaks Secret Scan",
        "gwdbx-flag-registry-gate",
        "nswag-freshness",
        "nswag-otp-freshness",
        "provider-boundary-gates",
        "stateless-gate",
        "docker",
        "olivium-dev/jeeb-gateway",
        "jeeb-production",
        "192.168.2.120",
        "jeeb-production-jeeb-gateway",
        "10000:8080:ingress",
        "@${IMAGE_DIGEST}",
        "docker service update",
        "--update-order start-first",
        "--update-failure-action pause",
        "--update-max-failure-ratio 0",
        "--health-cmd",
        "http://localhost:8080/health/ready",
        "SuperLogin__OpenMode=false",
        "DemoUsers__Enabled=false",
        "Features__DevEndpoints__Enabled=false",
        "Features__Swagger__Enabled=false",
        "Security__TokenMint__Enabled=true",
        "TestControlPlane__Enabled=false",
        "/api/User/super-login/users",
        "/swagger/index.html",
        "/__test/clock",
        "/auth/tokens",
        "401|403",
        "http://127.0.0.1:10000/health/ready",
        "forward-only deployment result is preserved for inspection and repair",
    )
    for token in required_contract:
        if token not in surface:
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} contract token missing: {token}"
            )

    shell = "\n".join(
        str(step.get("run", "")) for step in steps if isinstance(step, dict)
    ).replace("\\\n", " ")
    normalized_shell = re.sub(r"[\t ]+", " ", shell)
    for match in re.finditer(r"\bservice\s+([a-z][a-z0-9-]*)\b", normalized_shell):
        verb = match.group(1)
        if verb not in {"inspect", "ps", "update"}:
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} forbidden service verb: {verb}"
            )
    forbidden_patterns = (
        r"\bservice\s+[\"']?\$\{?",
        r"[\"']?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[\"']?\s+service\s+",
        r"\bstack\s+",
        r"/services/create\b",
        r"\bPOST\b[^\n]*/services(?:\b|/)",
    )
    for pattern in forbidden_patterns:
        if re.search(pattern, normalized_shell, re.I):
            raise ValueError(
                f"{PRODUCTION_WORKFLOW_NAME}:{job_name} dynamic/API mutation authority detected"
            )


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
        if name == PRODUCTION_WORKFLOW_NAME:
            validate_production_authority(document, job_name, job, steps)
            continue
        owner = steps[0]
        expected_owner_keys = (
            {"name", "if", "run"}
            if name == "jeeb-staging-deploy.yml"
            else {"name", "run"}
        )
        if not isinstance(owner, dict) or set(owner) != expected_owner_keys:
            raise ValueError(f"{name}:{job_name} first executable step is not canonical")
        if owner.get("name") != OWNER_STEP_NAME:
            raise ValueError(f"{name}:{job_name} owner step is not first")
        if name == "jeeb-staging-deploy.yml":
            if owner.get("if") != SECURITY_CUTOVER_OWNER_IF:
                raise ValueError(f"{name}:{job_name} security-cutover owner condition drifted")
            dispatch = document.get("on", {}).get("workflow_dispatch")
            inputs = dispatch.get("inputs") if isinstance(dispatch, dict) else None
            if inputs != STAGING_DISPATCH_INPUTS:
                raise ValueError(f"{name}:{job_name} deployment-mode input drifted")
            if len(steps) < 6:
                raise ValueError(f"{name}:{job_name} security-cutover gates are incomplete")
            provider_expand_hold = steps[1]
            if not isinstance(provider_expand_hold, dict) or set(provider_expand_hold) != {
                "name",
                "if",
                "run",
            }:
                raise ValueError(f"{name}:{job_name} provider expand hold shape drifted")
            if provider_expand_hold.get("name") != PROVIDER_EXPAND_HOLD_NAME:
                raise ValueError(f"{name}:{job_name} provider expand hold name drifted")
            if provider_expand_hold.get("if") != PROVIDER_EXPAND_HOLD_IF:
                raise ValueError(f"{name}:{job_name} provider expand hold condition drifted")
            provider_expand_body = provider_expand_hold.get("run")
            if not isinstance(provider_expand_body, str) or tuple(
                provider_expand_body.splitlines()
            ) != PROVIDER_EXPAND_HOLD_LINES:
                raise ValueError(f"{name}:{job_name} provider expand hold body drifted")
            security_gate = steps[2]
            if not isinstance(security_gate, dict) or set(security_gate) != {
                "name",
                "if",
                "env",
                "run",
            }:
                raise ValueError(f"{name}:{job_name} security owner gate shape drifted")
            if security_gate.get("name") != SECURITY_CUTOVER_GATE_NAME:
                raise ValueError(f"{name}:{job_name} security owner gate name drifted")
            if security_gate.get("if") != SECURITY_CUTOVER_GATE_IF:
                raise ValueError(f"{name}:{job_name} security owner gate condition drifted")
            if security_gate.get("env") != SECURITY_CUTOVER_GATE_ENV:
                raise ValueError(f"{name}:{job_name} security owner identity inputs drifted")
            security_body = security_gate.get("run")
            if not isinstance(security_body, str) or tuple(
                security_body.splitlines()
            ) != SECURITY_CUTOVER_GATE_LINES:
                raise ValueError(f"{name}:{job_name} security owner gate body drifted")
            accepted = run_security_cutover_gate(
                security_body, "oudaykhaled", "oudaykhaled"
            )
            if accepted.returncode != 0 or accepted.stdout or accepted.stderr:
                raise ValueError(f"{name}:{job_name} designated owner pair was rejected")
            for actor, triggering_actor in (
                ("OudayKhaled", "oudaykhaled"),
                ("oudaykhaled", "OudayKhaled"),
                ("another-actor", "oudaykhaled"),
                ("oudaykhaled", "rerun-by-another-actor"),
            ):
                rejected = run_security_cutover_gate(
                    security_body, actor, triggering_actor
                )
                if (
                    rejected.returncode != 1
                    or rejected.stdout
                    or rejected.stderr
                    != "::error::Protected staging deployments require the designated owner.\n"
                    or actor in rejected.stderr
                    or triggering_actor in rejected.stderr
                ):
                    raise ValueError(
                        f"{name}:{job_name} security owner gate is not fail-closed and sanitized"
                    )
            if set(steps[3]) != {"uses"} or not str(steps[3]["uses"]).startswith(
                "actions/checkout@"
            ):
                raise ValueError(f"{name}:{job_name} protected checkout is not after owner gate")
            freeze = steps[4]
            if freeze != {
                "name": "Prove public OTP verification freeze before any deploy action",
                "if": "${{ inputs.deployment_mode != 'devtool-reassert' }}",
                "run": "bash scripts/verify-staging-otp-verify-freeze.sh",
            }:
                raise ValueError(f"{name}:{job_name} first security-cutover gate drifted")
            first_mutation = next(
                (
                    index
                    for index, step in enumerate(steps)
                    if any(
                        marker in json.dumps(step, sort_keys=True)
                        for marker in MUTATION_STEP_MARKERS
                    )
                ),
                None,
            )
            if first_mutation is None or first_mutation <= 3:
                raise ValueError(f"{name}:{job_name} mutation precedes the public freeze")
            cleanup_indices = [
                index
                for index, step in enumerate(steps)
                if step.get("name") == "Clean up isolated remote Docker credentials"
            ]
            if cleanup_indices != [len(steps) - 1]:
                raise ValueError(f"{name}:{job_name} remote credential cleanup count drifted")
            cleanup = steps[cleanup_indices[0]]
            if set(cleanup) != {"name", "if", "run"}:
                raise ValueError(f"{name}:{job_name} remote credential cleanup shape drifted")
            if cleanup.get("if") != CREDENTIAL_CLEANUP_CONDITION:
                raise ValueError(f"{name}:{job_name} remote credential cleanup is not failure-safe")
            if tuple(str(cleanup.get("run", "")).splitlines()) != CREDENTIAL_CLEANUP_LINES:
                raise ValueError(f"{name}:{job_name} remote credential cleanup body drifted")
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
    "jeeb-production-deploy.yml": workflow_dir / "jeeb-production-deploy.yml",
    "jeeb-staging-deploy.yml": workflow_dir / "jeeb-staging-deploy.yml",
    "jeeb-staging-state-auth-smoke.yml": workflow_dir / "jeeb-staging-state-auth-smoke.yml",
}
if len(workflow_authority_paths) + 2 != len(expected_mutation_inventory):
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

    cleanup_name_spoof = copy.deepcopy(document)
    spoofed_step = find_later_mutation_step(cleanup_name_spoof)
    spoofed_step["name"] = "Clean up isolated remote Docker credentials"
    spoofed_step["if"] = CREDENTIAL_CLEANUP_CONDITION
    spoofed_step["run"] = "docker service update --force jeeb-gateway"
    assert_workflow_rejected("cleanup-name mutation spoof", name, cleanup_name_spoof)

    if name == PRODUCTION_WORKFLOW_NAME:
        for description, payload in (
            ("variable docker service create", 'ENGINE=docker; "$ENGINE" service create unsafe'),
            ("multiline docker service create", "docker service \\\n create unsafe"),
            ("dynamic service verb", 'ACTION=create; docker service "$ACTION" unsafe'),
            (
                "Docker Engine service create API",
                "curl --unix-socket /var/run/docker.sock -X POST http://localhost/services/create",
            ),
        ):
            mutation = copy.deepcopy(document)
            step = find_later_mutation_step(mutation)
            step["run"] = f"{step.get('run', '')}\n{payload}\n"
            assert_workflow_rejected(description, name, mutation)

        for description, token in (
            ("token mint gate removed", "Security__TokenMint__Enabled=true"),
            ("test control-plane gate removed", "TestControlPlane__Enabled=false"),
            ("process-only health gate removed", "--health-cmd"),
        ):
            mutation = copy.deepcopy(document)
            deploy_job = mutation["jobs"]["deploy"]
            for step in deploy_job["steps"]:
                if isinstance(step, dict) and isinstance(step.get("run"), str):
                    rewritten = []
                    for line in step["run"].splitlines():
                        if token in line and not line.lstrip().startswith("#"):
                            line = line.replace(token, "")
                        rewritten.append(line)
                    step["run"] = "\n".join(rewritten) + f"\n# retained decoy: {token}\n"
            assert_workflow_rejected(description + " with comment decoy", name, mutation)

        for check_name in PRODUCTION_EXACT_SHA_CHECKS:
            mutation = copy.deepcopy(document)
            ci_step = mutation["jobs"]["deploy"]["steps"][4]
            ci_step["run"] = ci_step["run"].replace(
                check_name,
                "",
                1,
            ) + f"\n# retained exact-SHA decoy: {check_name}\n"
            assert_workflow_rejected(
                f"exact-SHA check removed with comment decoy: {check_name}",
                name,
                mutation,
            )

        unenforced_ci = copy.deepcopy(document)
        ci_step = unenforced_ci["jobs"]["deploy"]["steps"][4]
        assertion = (
            '  [ "$state" = completed:success ] || {\n'
            '    echo "::error::Exact-SHA check \'$required\' is not successful ($state)." >&2\n'
            "    exit 69\n"
            "  }"
        )
        ci_step["run"] = ci_step["run"].replace(
            assertion,
            '  echo "$required"\n  # completed:success assertion removed',
        )
        assert_workflow_rejected(
            "exact-SHA names retained while success enforcement is removed",
            name,
            unenforced_ci,
        )

        neutralized_readiness = copy.deepcopy(document)
        deploy_step = next(
            step
            for step in neutralized_readiness["jobs"]["deploy"]["steps"]
            if step.get("name") == "Update production gateway and verify production posture"
        )
        deploy_step["run"] = deploy_step["run"].replace(
            'if curl -fsS --max-time 5 "$base_url/health/ready" >/dev/null; then',
            'if true || curl -fsS --max-time 5 "$base_url/health/ready" >/dev/null; then',
        )
        assert_workflow_rejected(
            "bounded readiness function neutralized while tokens remain",
            name,
            neutralized_readiness,
        )

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

    if name == "jeeb-staging-deploy.yml":
        unsafe_condition = copy.deepcopy(document)
        unsafe_condition["jobs"]["deploy"]["steps"][0]["if"] = "${{ false }}"
        assert_workflow_rejected(
            "security-cutover owner condition widened", name, unsafe_condition
        )

        extra_mode = copy.deepcopy(document)
        extra_mode["on"]["workflow_dispatch"]["inputs"]["deployment_mode"][
            "options"
        ].append("bypass")
        assert_workflow_rejected("extra deployment mode", name, extra_mode)

        duplicate_cleanup = copy.deepcopy(document)
        duplicate_cleanup["jobs"]["deploy"]["steps"].append(
            copy.deepcopy(duplicate_cleanup["jobs"]["deploy"]["steps"][-1])
        )
        assert_workflow_rejected("duplicate credential cleanup", name, duplicate_cleanup)

        mutating_cleanup = copy.deepcopy(document)
        mutating_cleanup["jobs"]["deploy"]["steps"][-1]["run"] += (
            "\ndocker service update --force jeeb-staging-jeeb-gateway"
        )
        assert_workflow_rejected("credential cleanup mutation append", name, mutating_cleanup)

        missing_security_gate = copy.deepcopy(document)
        del missing_security_gate["jobs"]["deploy"]["steps"][2]
        assert_workflow_rejected(
            "security owner gate removed", name, missing_security_gate
        )

        actor_only = copy.deepcopy(document)
        actor_only["jobs"]["deploy"]["steps"][2]["run"] = (
            "set -euo pipefail\n"
            'if [ "$REQUESTING_ACTOR" != oudaykhaled ]; then\n'
            "  echo '::error::Security cutover requires the designated owner.' >&2\n"
            "  exit 1\n"
            "fi\n"
        )
        assert_workflow_rejected("triggering actor check removed", name, actor_only)

        triggering_only = copy.deepcopy(document)
        triggering_only["jobs"]["deploy"]["steps"][2]["run"] = (
            "set -euo pipefail\n"
            'if [ "$TRIGGERING_ACTOR" != oudaykhaled ]; then\n'
            "  echo '::error::Security cutover requires the designated owner.' >&2\n"
            "  exit 1\n"
            "fi\n"
        )
        assert_workflow_rejected("requesting actor check removed", name, triggering_only)

        for description, condition in (
            (
                "repository-owner authority widening",
                "${{ inputs.deployment_mode == 'security-cutover' && github.repository_owner == 'olivium-dev' }}",
            ),
            (
                "admin-name authority widening",
                "${{ inputs.deployment_mode == 'security-cutover' && github.actor == 'admin' }}",
            ),
            (
                "contains authority widening",
                "${{ inputs.deployment_mode == 'security-cutover' && contains(github.actor, 'oudaykhaled') }}",
            ),
        ):
            widened_authority = copy.deepcopy(document)
            widened_authority["jobs"]["deploy"]["steps"][2]["if"] = condition
            assert_workflow_rejected(description, name, widened_authority)

        late_security_gate = copy.deepcopy(document)
        steps = late_security_gate["jobs"]["deploy"]["steps"]
        steps[2], steps[3] = steps[3], steps[2]
        assert_workflow_rejected(
            "security owner gate moved after checkout", name, late_security_gate
        )

        post_freeze_security_gate = copy.deepcopy(document)
        steps = post_freeze_security_gate["jobs"]["deploy"]["steps"]
        steps[2], steps[4] = steps[4], steps[2]
        assert_workflow_rejected(
            "security owner gate moved after freeze", name, post_freeze_security_gate
        )

        rerun_bypass = copy.deepcopy(document)
        rerun_bypass["jobs"]["deploy"]["steps"][2]["env"][
            "TRIGGERING_ACTOR"
        ] = "${{ github.actor }}"
        assert_workflow_rejected(
            "rerun triggering actor replaced by requesting actor", name, rerun_bypass
        )

        missing_freeze = copy.deepcopy(document)
        del missing_freeze["jobs"]["deploy"]["steps"][4]
        assert_workflow_rejected("public freeze removed", name, missing_freeze)

        late_freeze = copy.deepcopy(document)
        steps = late_freeze["jobs"]["deploy"]["steps"]
        steps[4], steps[7] = steps[7], steps[4]
        assert_workflow_rejected("public freeze moved after SSH", name, late_freeze)

rotation_contract = subprocess.run(
    ["bash", "scripts/check-staging-probe-key-rotation-contract.sh"],
    cwd=repo_root,
    text=True,
    capture_output=True,
    check=False,
)
if rotation_contract.returncode != 0:
    raise SystemExit(
        "FAIL: protected staging probe-key rotation contract is invalid:\n"
        + rotation_contract.stdout
        + rotation_contract.stderr
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
    f"Structurally validated {len(workflow_authority_paths)} single-job workflow "
    "authorities, dynamically executed their owner steps under empty PATH, and "
    "rejected the adversarial workflow mutation suite."
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
    "ForwardedHeaders__KnownProxies__0",
    "probe_staging_untrusted_xff_contract",
    "scripts/probe-staging-untrusted-xff.sh",
    "add_env Gateway__PublicBaseUrl https://app.jeeb.fds-1.com",
    "add_env AdminPortal__AllowedOrigins__0 https://app.jeeb.fds-1.com",
    "add_env AdminPortal__AllowedOrigins__1 https://cms.jeeb.fds-1.com",
    "scripts/probe-staging-public-gateway-contract.sh",
    "posture_mode=posture",
    "posture_mode=devtool-posture",
    "scripts/staging-gateway-public-edge-backoff.sh",
    "scripts/test-super-login.sh https://app.jeeb.fds-1.com",
    "add_env Security__TokenMint__Enabled true",
    "add_env SuperLogin__OpenMode true",
    "add_env DemoUsers__Enabled true",
    "add_env Features__DevEndpoints__Enabled true",
    "add_env Features__Swagger__Enabled true",
    "scripts/staging-gateway-devtool-reassert-candidate.jq",
    "scripts/staging-gateway-readiness-backoff.sh",
    "staging phase=devtool-public-edge-stabilization result=started (redacted)",
    "scripts/staging-gateway-transaction-summary.sh",
    "scripts/staging-gateway-incumbent-devtool-posture.jq",
    "source scripts/staging-gateway-mutation-lock.sh",
    "staging_gateway_lock_acquire",
    "staging_gateway_lock_assert",
    "staging_gateway_lock_release",
):
    if token not in staging:
        raise SystemExit(f"FAIL: staging deploy lacks commit/image/runtime proof: {token}")
if "ForwardedHeaders__KnownNetworks" in staging:
    raise SystemExit("FAIL: staging deploy trusts a forwarded-header network range")
if "add_env ForwardedHeaders__KnownProxies" in staging:
    raise SystemExit("FAIL: staging deploy trusts the Swarm L4 ingress peer as an HTTP proxy")

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
