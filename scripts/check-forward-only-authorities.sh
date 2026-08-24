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


def is_required_gateway_rollback(path, label, line):
    """Allow only the fail-safe rollback forms required by gateway deploys.

    Both gateway workflows update one replica on a host-published port, so their
    order is deliberately stop-first. This narrow exception does not authorize
    rollback commands elsewhere and is backed by exact contract checks below.
    """
    if path not in {
        Path(".github/workflows/deploy-to-jeeb.yml"),
        Path(".github/workflows/jeeb-staging-deploy.yml"),
    }:
        return False
    stripped = line.strip()
    allowed = {
        "service rollback": re.compile(
            r'^docker service '
            + r'rollback --detach=false "(?:\$service|\\\$SVC)" '
            + r'\|\| (?:rollback_ok|recovery_ok)=false$'
        ),
        "automatic rollback": re.compile(
            r'^--update-failure-action ' + r'rollback$'
        ),
        "rollback option": re.compile(
            r'^--' + r'rollback-(?:order stop-first|parallelism 1|monitor 20s|failure-action pause)$'
        ),
    }
    return label in allowed and bool(allowed[label].fullmatch(stripped))


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
            if is_required_gateway_rollback(path, label, line):
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
for canary in (
    "docker service " + "\\  \n" + 'update --image "$IMAGE" app',
    'ENGINE=docker\n"$ENGINE" service ' + 'update --image "$IMAGE" app',
    "docker service " + "scale app=0",
    'ENGINE=docker\n"$ENGINE" service ' + "rollback app",
    'ENGINE=docker\n"$ENGINE" stack ' + 'deploy -c stack.yml app',
    'ENGINE=docker\n"$ENGINE" service ' + "\\  \n" + "rollback app",
):
    if not mutation_pattern.search(normalized_shell_source(canary)):
        raise SystemExit("FAIL: service mutation inventory misses an adversarial canary")
mutation_inventory = {
    path.relative_to(Path(".")).as_posix()
    for root in (Path(".github/workflows"), Path(".github/scripts"))
    for path in root.rglob("*")
    if path.is_file() and path.suffix in {".yml", ".yaml", ".sh"}
    and mutation_pattern.search(normalized_shell_source(path.read_text()))
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

runtime_verifier = Path("scripts/verify-swarm-service-image.sh").read_text()


def validate_runtime_verifier(text):
    required = (
        "initial|completed|rollback_completed) break",
        "updating|rollback_started) sleep 4",
        "initial|completed|rollback_completed) ;;",
    )
    missing = [token for token in required if token not in text]
    if missing:
        raise ValueError(f"rollback-aware runtime verifier drifted: {missing}")


validate_runtime_verifier(runtime_verifier)
for description, mutated in (
    (
        "rollback completed acceptance removed",
        runtime_verifier.replace("|rollback_completed", ""),
    ),
    (
        "rollback started polling removed",
        runtime_verifier.replace("|rollback_started", ""),
    ),
):
    try:
        validate_runtime_verifier(mutated)
    except ValueError:
        continue
    raise SystemExit(f"FAIL: runtime verifier negative control survived: {description}")

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
    'previous_image=',
    'Incumbent service image is not digest-pinned; recovery target is unsafe',
    '--update-order stop-first',
    '--update-failure-action ' + 'rollback',
    '--' + 'rollback-order stop-first',
    'docker service ' + 'rollback --detach=false "\\$SVC"',
    'Deployed service spec does not match the requested immutable digest',
    'REQUESTED_SERVICE: ${{ inputs.service_name }}',
    'REQUESTED_HOST: ${{ inputs.server_hostname }}',
    'STAGING_SSH_HOST: ${{ secrets.JEEB_STAGING_SSH_HOST }}',
    '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]',
    '[ "$REQUESTED_HOST" = "$STAGING_SSH_HOST" ]',
    "*[!a-zA-Z0-9_.-]*)",
    'canonical_service=$(ssh jeeb',
    '[ "$canonical_service" = jeeb-staging-jeeb-gateway ]',
):
    if token not in direct:
        raise SystemExit(f"FAIL: direct production deploy lacks commit/image/runtime proof: {token}")
for forbidden_direct in (
    'docker service ' + 'create',
    '--update-order start-first',
    '--update-failure-action pause',
):
    if forbidden_direct in direct:
        raise SystemExit(f"FAIL: direct host-mode deploy contains unsafe rollout behavior: {forbidden_direct}")

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
    "remove_secret_target jeeb_gateway_umjwt",
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
for required_rollout in (
    "--update-order stop-first",
    "--update-failure-action " + "rollback",
    "--" + "rollback-order stop-first",
    "docker service inspect '$service' --format '{{json .Spec}}'",
    "update?version=\\${expected_version}&rollback=" + "previous",
    "registryAuthFrom=previous-spec",
    "rollback CAS outcome did not reconcile to the exact incumbent",
    "docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'",
    "Incumbent service image is not digest-pinned",
):
    if required_rollout not in staging_authority:
        raise SystemExit(f"FAIL: staging safe-rollout contract missing: {required_rollout}")
for forbidden_rollout in (
    "--update-order start-first",
    "published=$published,target=$target,mode=ingress",
):
    if forbidden_rollout in staging_authority:
        raise SystemExit(f"FAIL: staging host-mode rollout drifted: {forbidden_rollout}")

public_probe = Path("scripts/probe-staging-public-gateway-contract.sh").read_text()
for token in (
    "https://jeeb.dev/errors/csrf_rejected",
    "https://jeeb.dev/errors/origin_rejected",
    "expect_status 404",
    "expect_status 400 'OTP request validation contract'",
    "expect_status 401 'Token mint without a privileged credential'",
    "expect_status 403 'Token mint with an invalid privileged credential'",
    "invalid-staging-mint-probe-credential",
    "--data '{}'",
):
    if token not in public_probe:
        raise SystemExit(f"FAIL: staging public contract probe missing: {token}")

ci_workflow = Path(".github/workflows/ci.yml").read_text()
unit_test_command = (
    "dotnet test tests/JeebGateway.UnitTests/JeebGateway.UnitTests.csproj"
)
for token in (
    "dotnet restore tests/JeebGateway.UnitTests/JeebGateway.UnitTests.csproj",
    "dotnet build tests/JeebGateway.UnitTests/JeebGateway.UnitTests.csproj",
    unit_test_command,
):
    if token not in ci_workflow:
        raise SystemExit(f"FAIL: CI does not compile and run the gateway unit suite: {token}")


def validate_direct_rollout(text):
    required = (
        "previous_image=",
        "--update-order stop-first",
        "--update-failure-action " + "rollback",
        "--" + "rollback-order stop-first",
        "docker service " + 'rollback --detach=false "\\$SVC"',
        "rollback_armed=false",
        "Reject the staging bootstrap target",
        '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]',
        '[ "$REQUESTED_HOST" = "$STAGING_SSH_HOST" ]',
        "*[!a-zA-Z0-9_.-]*)",
        'canonical_service=$(ssh jeeb',
        '[ "$canonical_service" = jeeb-staging-jeeb-gateway ]',
        "SSH_KNOWN_HOSTS: ${{ secrets.JEEB_SSH_KNOWN_HOSTS }}",
        "UserKnownHostsFile ~/.ssh/known_hosts",
        "StrictHostKeyChecking yes",
    )
    missing = [token for token in required if token not in text]
    forbidden = [
        token
        for token in (
            "docker service " + "create",
            "--update-order start-first",
            "--update-failure-action pause",
        )
        if token in text
    ]
    if missing or forbidden:
        raise ValueError(f"missing={missing}, forbidden={forbidden}")
    staging_guard = text.index(
        '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]'
    )
    host_guard = text.index('[ "$REQUESTED_HOST" = "$STAGING_SSH_HOST" ]')
    first_external_mutation = text.index("docker login")
    if not staging_guard < first_external_mutation or not host_guard < first_external_mutation:
        raise ValueError("staging target guards must run before any external mutation")
    canonical_guard = text.index('[ "$canonical_service" = jeeb-staging-jeeb-gateway ]')
    ssh_setup = text.index("Install cloudflared + write deploy key")
    first_build = text.index("docker build")
    first_remote_mutation = text.index("Remote GHCR login")
    if not ssh_setup < canonical_guard < first_external_mutation < first_build < first_remote_mutation:
        raise ValueError(
            "pinned SSH and canonical staging alias guard must precede registry/build/push mutation"
        )


def validate_staging_rollout(text):
    probe_command = "bash scripts/probe-staging-public-gateway-contract.sh"
    required = (
        "add_env Security__TokenMint__Enabled true",
        "remove_secret_target jeeb_gateway_umjwt",
        "--update-failure-action " + "rollback",
        "--" + "rollback-order stop-first",
        "capture_remote_spec() {",
        "docker service inspect '$service' --format '{{json .Spec}}'",
        "update?version=\\${expected_version}&rollback=" + "previous",
        "registryAuthFrom=previous-spec",
        "docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'",
        "probe_staging_realtime_descriptor",
        "verify_bootstrap_flags",
        "group: jeeb-staging-gateway-mutation",
        "source scripts/staging-gateway-mutation-lock.sh",
        "staging_gateway_lock_acquire",
        "staging_gateway_lock_assert",
        "staging_gateway_lock_release",
        'capture_remote_spec "$terminal_spec" "$terminal_version" "$terminal_id"',
        'cmp -s "$terminal_spec" "$incumbent_spec"',
        "verify_candidate_readiness",
    )
    missing = [token for token in required if token not in text]
    if missing or text.count(probe_command) != 2:
        raise ValueError(
            f"missing={missing}, public_probe_count={text.count(probe_command)}"
        )
    armed = text.index("rollback_armed=true")
    forward_public = text.index(probe_command, armed)
    disarmed = text.index("rollback_armed=false", armed)
    if not armed < forward_public < disarmed:
        raise ValueError("public gates are not inside the armed recovery interval")
    update = text.index("docker service update --detach=false", armed)
    candidate = text.index(
        'capture_remote_spec "$candidate_spec" "$candidate_version" "$candidate_id"',
        update,
    )
    readiness = text.index("verify_candidate_readiness", candidate)
    if not update < candidate < readiness < forward_public:
        raise ValueError("candidate full Spec must be captured before fallible readiness/public gates")


def validate_shared_staging_mutator_lock(deploy_text, smoke_text):
    markers = (
        "group: jeeb-staging-gateway-mutation",
        "source scripts/staging-gateway-mutation-lock.sh",
        ".jeeb-deploy/locks/jeeb-staging-gateway.owner",
        "staging_gateway_lock_acquire",
        "staging_gateway_lock_assert",
        "staging_gateway_lock_release",
    )
    for name, text in (("deploy", deploy_text), ("state-auth", smoke_text)):
        missing = [marker for marker in markers if marker not in text]
        if missing:
            raise ValueError(f"{name} staging mutator lacks shared lock: {missing}")
    owner_check = smoke_text.index(
        '[ "$(cat "$lock_owner_file" 2>/dev/null)" = "$expected_lock_owner" ]'
    )
    mutation = smoke_text.index("docker service update --force")
    if not owner_check < mutation:
        raise ValueError("state-auth mutation can run before shared-lock ownership proof")


def validate_unit_ci(text):
    if unit_test_command not in text:
        raise ValueError("unit test command missing")


validate_direct_rollout(direct)
validate_staging_rollout(staging_authority)
validate_shared_staging_mutator_lock(staging_authority, smoke)
validate_unit_ci(ci_workflow)

negative_controls = (
    (
        "direct automatic recovery changed to pause",
        validate_direct_rollout,
        direct.replace(
            "--update-failure-action " + "rollback",
            "--update-failure-action pause",
            1,
        ),
    ),
    (
        "direct host-mode rollout changed to start-first",
        validate_direct_rollout,
        direct.replace("--update-order stop-first", "--update-order start-first", 1),
    ),
    (
        "direct deploy reintroduces service creation",
        validate_direct_rollout,
        direct + "\n" + "docker service " + "create app\n",
    ),
    (
        "direct staging service guard removed",
        validate_direct_rollout,
        direct.replace(
            '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]',
            '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gatewa ]',
            1,
        ),
    ),
    (
        "direct staging host guard removed",
        validate_direct_rollout,
        direct.replace(
            '[ "$REQUESTED_HOST" = "$STAGING_SSH_HOST" ]',
            '[ "$REQUESTED_HOST" = "" ]',
            1,
        ),
    ),
    (
        "direct staging service guard moved after first mutation",
        validate_direct_rollout,
        direct.replace(
            '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]',
            ':',
            1,
        ).replace(
            "docker login ${{ env.REGISTRY }}",
            "docker login ${{ env.REGISTRY }}\n"
            + '[ "$REQUESTED_SERVICE" = jeeb-staging-jeeb-gateway ]',
            1,
        ),
    ),
    (
        "direct canonical staging alias guard removed",
        validate_direct_rollout,
        direct.replace(
            '[ "$canonical_service" = jeeb-staging-jeeb-gateway ]',
            '[ "$canonical_service" = jeeb-staging-jeeb-gatewa ]',
            1,
        ),
    ),
    (
        "direct canonical staging alias guard moved after local registry login",
        validate_direct_rollout,
        direct.replace(
            '[ "$canonical_service" = jeeb-staging-jeeb-gateway ]',
            ":",
            1,
        ).replace(
            "docker login ${{ env.REGISTRY }}",
            "docker login ${{ env.REGISTRY }}\n"
            + '[ "$canonical_service" = jeeb-staging-jeeb-gateway ]',
            1,
        ),
    ),
    (
        "direct service-name input validation removed",
        validate_direct_rollout,
        direct.replace("*[!a-zA-Z0-9_.-]*)", "*)", 1),
    ),
    (
        "staging token mint gate disabled",
        validate_staging_rollout,
        staging_authority.replace(
            "add_env Security__TokenMint__Enabled true",
            "add_env Security__TokenMint__Enabled false",
            1,
        ),
    ),
    (
        "staging exact-incumbent public recheck removed",
        validate_staging_rollout,
        staging_authority.replace(
            "bash scripts/probe-staging-public-gateway-contract.sh", "", 1
        ),
    ),
    (
        "staging terminal incumbent Spec recheck removed",
        validate_staging_rollout,
        staging_authority.replace(
            'capture_remote_spec "$terminal_spec" "$terminal_version" "$terminal_id"',
            ":",
            1,
        ),
    ),
    (
        "staging candidate capture moved after readiness",
        validate_staging_rollout,
        staging_authority.replace(
            '          capture_remote_spec "$candidate_spec" "$candidate_version" "$candidate_id" || {',
            "          : || {",
            1,
        ).replace(
            "          verify_candidate_readiness",
            '          capture_remote_spec "$candidate_spec" "$candidate_version" "$candidate_id"\n'
            "          verify_candidate_readiness",
            1,
        ),
    ),
    (
        "optional UMJWT target cleanup removed",
        validate_staging_rollout,
        staging_authority.replace("remove_secret_target jeeb_gateway_umjwt", ":", 1),
    ),
    (
        "unit test execution removed",
        validate_unit_ci,
        ci_workflow.replace(unit_test_command, "", 1),
    ),
)
for description, validator, mutated in negative_controls:
    try:
        validator(mutated)
    except ValueError:
        continue
    raise SystemExit(f"FAIL: deployment policy negative control survived: {description}")

for description, mutated_deploy, mutated_smoke in (
    (
        "staging deploy concurrency key drifted",
        staging_authority.replace(
            "group: jeeb-staging-gateway-mutation",
            "group: jeeb-staging-gateway-deploy",
            1,
        ),
        smoke,
    ),
    (
        "state-auth shared lock removed",
        staging_authority,
        smoke.replace("staging_gateway_lock_acquire", ":", 1),
    ),
):
    try:
        validate_shared_staging_mutator_lock(mutated_deploy, mutated_smoke)
    except ValueError:
        continue
    raise SystemExit(f"FAIL: shared staging lock negative control survived: {description}")

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
