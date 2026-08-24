#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
import json
import re
from pathlib import Path

workflow_path = Path(".github/workflows/jeeb-staging-deploy.yml")
state_auth_workflow_path = Path(".github/workflows/jeeb-staging-state-auth-smoke.yml")
program_path = Path("src/JeebGateway/Program.cs")
auth_path = Path("src/JeebGateway/Operations/RealtimeProbe/RealtimeProbeAuthentication.cs")
replay_path = Path("src/JeebGateway/Operations/RealtimeProbe/RealtimeProbeReplayStore.cs")
service_path = Path("src/JeebGateway/Operations/RealtimeProbe/RealtimeProbeDescriptorService.cs")
credential_config_path = Path(
    "src/JeebGateway/Operations/RealtimeProbe/RealtimeProbeCredentialConfiguration.cs"
)
endpoint_path = Path("src/JeebGateway/Operations/RealtimeProbe/StagingRealtimeProbeEndpoint.cs")
middleware_path = Path("src/JeebGateway/Security/ApiKeyAuthenticationMiddleware.cs")
contract_path = Path(
    "src/JeebGateway/contracts/producer/staging-realtime-probe.openapi.json"
)

documents = {
    "workflow": workflow_path.read_text(),
    "state-auth workflow": state_auth_workflow_path.read_text(),
    "program": program_path.read_text(),
    "authenticator": auth_path.read_text(),
    "replay store": replay_path.read_text(),
    "descriptor service": service_path.read_text(),
    "credential configuration": credential_config_path.read_text(),
    "endpoint": endpoint_path.read_text(),
    "API-key middleware": middleware_path.read_text(),
}


def require(document, markers):
    text = documents[document]
    missing = [marker for marker in markers if marker not in text]
    if missing:
        raise SystemExit(
            f"FAIL: {document} staging realtime probe contract drifted; missing {missing}"
        )


require(
    "workflow",
    (
        '[ "$GITHUB_REF" = "refs/heads/${DEFAULT_BRANCH}" ]',
        "JEEB_STAGING_WSS_PROBE_MINT_KEY: ${{ secrets.JEEB_STAGING_WSS_PROBE_MINT_KEY }}",
        ': "${JEEB_STAGING_WSS_PROBE_MINT_KEY:?JEEB_STAGING_WSS_PROBE_MINT_KEY is required}"',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JWT_SIGNING_KEY" ]',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JEEB_RTC_GUARDIAN_SECRET_KEY" ]',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JEEB_RTC_MEMBERSHIP_TICKET_KEY" ]',
        "bash scripts/assert-distinct-staging-signing-keys.sh",
        'stream_secret "$probe_secret_name" "$JEEB_STAGING_WSS_PROBE_MINT_KEY"',
        "add_env Operations__RealtimeProbe__MintKeyFile /run/secrets/staging_wss_probe_mint_key",
        "add_env Services__Realtime__GuardianSecretFile /run/secrets/realtime_guardian_secret",
        "add_env Services__Realtime__MembershipTicketSigningKeyFile /run/secrets/realtime_membership_ticket_key",
        "add_env Services__Realtime__PublicSocketUrl wss://app.jeeb.fds-1.com/socket/websocket",
        'add_rotated_secret "$probe_secret_name" staging_wss_probe_mint_key',
        "uid=65532,gid=65532,mode=0400",
        "add_env ASPNETCORE_ENVIRONMENT Staging",
    ),
)
if "add_env Operations__RealtimeProbe__MintKey " in documents["workflow"]:
    raise SystemExit("FAIL: staging workflow puts the probe mint key in the service environment")


def validate_bootstrap_workflow(text):
    required = (
        "Owner block - forward-only promotion pending",
        "::error::Forward-only promotion pending owner-approved failure handling",
        "add_env FeatureFlags__UseUpstream__Chat false",
        "add_env FeatureFlags__UseUpstream__Realtime false",
        "capture_remote_spec() {",
        "docker service inspect '$service' --format '{{json .Spec}}'",
        "docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'",
        'chmod 600 "$snapshot"',
        'cmp -s "$pre_update_spec" "$incumbent_spec"',
        'cmp -s "$pre_update_version" "$incumbent_version"',
        'cmp -s "$pre_update_id" "$incumbent_id"',
        "verify_exact_candidate_after_checks() {",
        'capture_remote_spec "$final_spec" "$final_version" "$final_id"',
        'cmp -s "$final_spec" "$candidate_spec"',
        'cmp -s "$final_version" "$candidate_version"',
        'cmp -s "$final_id" "$candidate_id"',
        "--update-failure-action pause",
        "source scripts/staging-gateway-mutation-lock.sh",
        'staging_gateway_lock_init jeeb-staging "$secret_stage"',
        "staging_gateway_lock_acquire",
        "staging_gateway_lock_assert",
        "staging_gateway_lock_release",
        'tolower($1) == tolower(expected)',
        "matches == 1 && exact_false == 1",
        "verify_bootstrap_flags",
        "probe_staging_realtime_descriptor",
        'STAGING_REALTIME_PROBE_KEY_FILE="$probe_key_file" python3',
        'PATH = "/internal/ops/staging/realtime-probe-descriptor"',
        "if malformed_status != 400:",
        "if forged_status != 403:",
        "if status != 200:",
        "if replay_status != 409:",
        "if set(descriptor) != expected_fields:",
        "if not 30 <= ttl <= 900:",
        '"no-store" not in',
        'descriptor["conversationId"] != conversation_id',
        'descriptor["topic"] != "jeeb:chat:" + conversation_id',
        'descriptor["socketUrl"] != "wss://app.jeeb.fds-1.com/socket/websocket"',
    )
    missing = [marker for marker in required if marker not in text]
    if missing:
        raise ValueError(f"missing forward-only bootstrap markers: {missing}")

    forbidden = (
        "docker service " + "rollback",
        "--update-failure-action " + "rollback",
        "--" + "rollback-order",
        "--" + "rollback-parallelism",
        "--" + "rollback-monitor",
        "--" + "rollback-failure-action",
        "&rollback=" + "previous",
        "recover_exact_" + "incumbent",
        "rollback_" + "armed",
    )
    present = [marker for marker in forbidden if marker in text]
    if present:
        raise ValueError(f"automatic recovery behavior remains: {present}")

    dispatch_header = text[: text.index("permissions:")]
    if re.search(r"(?m)^\s+inputs:\s*$", dispatch_header) or "${{ inputs." in text:
        raise ValueError("staging bootstrap exposes a callable activation input")
    for authority in ("Chat", "Realtime"):
        false_lock = f"add_env FeatureFlags__UseUpstream__{authority} false"
        true_lock = f"add_env FeatureFlags__UseUpstream__{authority} true"
        if text.count(false_lock) != 1 or true_lock in text:
            raise ValueError(f"staging bootstrap authority drifted: {authority}")

    blocker = text.index("Owner block - forward-only promotion pending")
    blocker_exit = text.index("exit 1", blocker)
    first_external_mutation = min(
        text.index("docker/login-action@", blocker),
        text.index("docker/build-push-action@", blocker),
        text.index("ssh jeeb-staging", blocker),
        text.index("docker service update --detach=false", blocker),
    )
    if not blocker < blocker_exit < first_external_mutation:
        raise ValueError("loud owner block does not precede every external mutation")
    if "if: always()" in text:
        raise ValueError("an always() step can bypass the owner block")

    pre_update = text.index(
        'capture_remote_spec "$pre_update_spec" "$pre_update_version" "$pre_update_id"'
    )
    update = text.index("docker service update --detach=false")
    candidate = text.index(
        'capture_remote_spec "$candidate_spec" "$candidate_version" "$candidate_id"',
        update,
    )
    readiness = text.index("verify_candidate_readiness", candidate)
    false_flags = text.index("verify_bootstrap_flags", candidate)
    verifier = text.index("scripts/verify-swarm-service-image.sh", false_flags)
    public_probe = text.index(
        "bash scripts/probe-staging-public-gateway-contract.sh", verifier
    )
    descriptor_probe = text.index("probe_staging_realtime_descriptor", public_probe)
    final_candidate = text.index(
        "verify_exact_candidate_after_checks", descriptor_probe
    )
    if not pre_update < update < candidate < readiness < false_flags < verifier < public_probe < descriptor_probe < final_candidate:
        raise ValueError("candidate verification order drifted")


def validate_shared_staging_lock(deploy_text, state_auth_text):
    markers = (
        "group: jeeb-staging-gateway-mutation",
        "source scripts/staging-gateway-mutation-lock.sh",
        ".jeeb-deploy/locks/jeeb-staging-gateway.owner",
        "staging_gateway_lock_acquire",
        "staging_gateway_lock_assert",
        "staging_gateway_lock_release",
    )
    for name, text in (("deploy", deploy_text), ("state-auth", state_auth_text)):
        missing = [marker for marker in markers if marker not in text]
        if missing:
            raise ValueError(f"{name} staging mutator lacks shared lock: {missing}")
    mutation = state_auth_text.index(
        "docker service update --force --update-failure-action pause"
    )
    owner_check = state_auth_text.index(
        '[ "$(cat "$lock_owner_file" 2>/dev/null)" = "$expected_lock_owner" ]'
    )
    if owner_check > mutation:
        raise ValueError("state-auth mutator does not prove shared lock ownership before mutation")


workflow = documents["workflow"]
validate_bootstrap_workflow(workflow)
validate_shared_staging_lock(workflow, documents["state-auth workflow"])

negative_controls = (
    (
        "owner block removed",
        workflow.replace("Owner block - forward-only promotion pending", "Promotion gate", 1),
    ),
    (
        "automatic recovery reintroduced",
        workflow.replace(
            "--update-failure-action pause",
            "--update-failure-action " + "rollback",
            1,
        ),
    ),
    (
        "chat bootstrap lock removed",
        workflow.replace("add_env FeatureFlags__UseUpstream__Chat false", "", 1),
    ),
    (
        "realtime bootstrap activated",
        workflow.replace(
            "add_env FeatureFlags__UseUpstream__Realtime false",
            "add_env FeatureFlags__UseUpstream__Realtime true",
            1,
        ),
    ),
    (
        "callable activation input added",
        workflow.replace(
            "  workflow_dispatch:",
            "  workflow_dispatch:\n    inputs:\n      activate_realtime:\n        type: boolean",
            1,
        ),
    ),
    (
        "full Spec reduced to image",
        workflow.replace("{{json .Spec}}", "{{.Spec.TaskTemplate.ContainerSpec.Image}}"),
    ),
    (
        "final candidate identity gate removed",
        workflow.replace("          verify_exact_candidate_after_checks\n", "", 1),
    ),
    (
        "case-insensitive duplicate bootstrap flag guard removed",
        workflow.replace("matches == 1 && exact_false == 1", "exact_false >= 1", 1),
    ),
)
for description, mutated in negative_controls:
    try:
        validate_bootstrap_workflow(mutated)
    except (ValueError, StopIteration):
        continue
    raise SystemExit(f"FAIL: workflow negative control survived: {description}")

require(
    "program",
    (
        "AddStagingRealtimeProbe(builder.Configuration, builder.Environment)",
        "AddSource(RealtimeProbeTelemetry.ActivitySourceName)",
        "app.MapStagingRealtimeProbe()",
    ),
)
require(
    "authenticator",
    (
        '"v1\\nPOST\\n"',
        "MaximumClockSkewSeconds = 60",
        'Guid.TryParseExact(nonce, "D"',
        "IsLowercaseSha256Hex(signature)",
        "CryptographicOperations.FixedTimeEquals(expected, supplied)",
        "MinimumKeyBytes = 32",
        'RequiredMintKeyFile = "/run/secrets/staging_wss_probe_mint_key"',
        "File.ReadAllBytes(path)",
    ),
)
require(
    "replay store",
    (
        'KeyPrefix = "jeeb:ops:realtime-probe:nonce:"',
        "TimeSpan.FromSeconds(120)",
        '"SET"',
        'new object[] { key, value, "NX", "EX", 120L }',
        "CommandFlags.DemandMaster",
        "RealtimeProbeReplayReservation.Unavailable",
    ),
)
require(
    "credential configuration",
    (
        'GuardianSecretFile = "/run/secrets/realtime_guardian_secret"',
        '"/run/secrets/realtime_membership_ticket_key"',
        "string.IsNullOrWhiteSpace(options.GuardianSecret)",
        "RealtimeProbeDescriptorService.ExactGuardianIssuer",
        "RealtimeProbeDescriptorService.ExactPublicSocketUrl",
    ),
)
require(
    "descriptor service",
    (
        '"edge-probe-" + nonce',
        '"jeeb:chat:" + conversationId',
        'const string role = "client"',
        "RealtimeGuardianTokenIssuer.SubscribeOnly",
        "CredentialLifetime = TimeSpan.FromSeconds(120)",
        'ExactGuardianIssuer = "live_comm"',
        'ExactPublicSocketUrl = "wss://app.jeeb.fds-1.com/socket/websocket"',
    ),
)
require(
    "endpoint",
    (
        'Route = "/internal/ops/staging/realtime-probe-descriptor"',
        "if (!environment.IsStaging())",
        "if (!app.Environment.IsStaging())",
        ".AllowAnonymous()",
        "StatusCodes.Status409Conflict",
        "StatusCodes.Status503ServiceUnavailable",
        'context.Response.Headers.CacheControl = "no-store"',
    ),
)
require("API-key middleware", ("StagingRealtimeProbeEndpoint.Route",))

contract = json.loads(contract_path.read_text())
if contract["openapi"] != "3.1.0":
    raise SystemExit("FAIL: producer contract must remain OpenAPI 3.1.0")
path = "/internal/ops/staging/realtime-probe-descriptor"
operation = contract["paths"][path]["post"]
header_names = {parameter["name"] for parameter in operation["parameters"]}
required_headers = {
    "X-Jeeb-Staging-Probe-Timestamp",
    "X-Jeeb-Staging-Probe-Nonce",
    "X-Jeeb-Staging-Probe-Signature",
}
if header_names != required_headers:
    raise SystemExit("FAIL: producer OpenAPI HMAC headers drifted")
timestamp = next(
    parameter
    for parameter in operation["parameters"]
    if parameter["name"] == "X-Jeeb-Staging-Probe-Timestamp"
)
if timestamp["schema"].get("pattern") != r"^(0|[1-9][0-9]*)$":
    raise SystemExit("FAIL: producer OpenAPI timestamp canonical form drifted")
if set(operation["responses"]) != {"200", "400", "401", "403", "409", "503"}:
    raise SystemExit("FAIL: producer OpenAPI status contract drifted")
descriptor = contract["components"]["schemas"]["RealtimeProbeDescriptor"]
required_fields = {
    "conversationId",
    "topic",
    "roleInConvo",
    "socketUrl",
    "token",
    "ticket",
    "expiresAt",
}
if set(descriptor["required"]) != required_fields:
    raise SystemExit("FAIL: producer OpenAPI descriptor completeness drifted")
for credential_field in ("token", "ticket"):
    schema = descriptor["properties"][credential_field]
    if schema.get("readOnly") is not True or "writeOnly" in schema:
        raise SystemExit(
            f"FAIL: producer OpenAPI {credential_field} must be response-only"
        )

print("Staging realtime probe, fail-visible deploy block, and forward-only contracts are exact.")
PY

bash scripts/test-staging-gateway-mutation-lock.sh
bash scripts/test-assert-distinct-staging-signing-keys.sh
