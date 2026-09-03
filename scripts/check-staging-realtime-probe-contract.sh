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
transaction_path = Path("scripts/staging-gateway-spec-recovery.sh")
authenticated_probe_path = Path("scripts/probe-staging-authenticated-realtime.py")
untrusted_xff_probe_path = Path("scripts/probe-staging-untrusted-xff.sh")
candidate_contract_path = Path("scripts/staging-gateway-candidate-contract.jq")

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
    "Spec transaction": transaction_path.read_text(),
    "authenticated probe": authenticated_probe_path.read_text(),
    "untrusted XFF probe": untrusted_xff_probe_path.read_text(),
    "candidate contract": candidate_contract_path.read_text(),
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
        'GITHUB_REF_PROTECTED: ${{ github.ref_protected }}',
        '[ "$GITHUB_REF_PROTECTED" = true ]',
        '[ "$REQUESTED_REPOSITORY" = "olivium-dev/jeeb-gateway" ]',
        "environment: staging",
        'StrictHostKeyChecking yes',
        'UserKnownHostsFile ~/.ssh/known_hosts',
        '[ "$(hostname -s)" = "olivium-ephemerals" ]',
        'grep -Fxc "192.168.2.20"',
        '[ "$approved_ip_count" -eq 1 ]',
        "Preflight canonical Swarm ingress topology",
        '"10000:8080:ingress"',
        "JEEB_STAGING_WSS_PROBE_MINT_KEY: ${{ secrets.JEEB_STAGING_WSS_PROBE_MINT_KEY }}",
        ': "${JEEB_STAGING_WSS_PROBE_MINT_KEY:?JEEB_STAGING_WSS_PROBE_MINT_KEY is required}"',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JWT_SIGNING_KEY" ]',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JEEB_RTC_GUARDIAN_SECRET_KEY" ]',
        '[ "$JEEB_STAGING_WSS_PROBE_MINT_KEY" != "$JEEB_RTC_MEMBERSHIP_TICKET_KEY" ]',
        "bash scripts/assert-distinct-staging-signing-keys.sh",
        'stream_secret "$probe_secret_name" "$JEEB_STAGING_WSS_PROBE_MINT_KEY"',
        "add_env Operations__RealtimeProbe__MintKeyFile /run/secrets/staging_wss_probe_mint_key",
        "ForwardedHeaders__KnownProxies__0",
        "probe_staging_untrusted_xff_contract",
        "scripts/probe-staging-untrusted-xff.sh",
        "add_env Services__Realtime__GuardianSecretFile /run/secrets/realtime_guardian_secret",
        "add_env Services__Realtime__MembershipTicketSigningKeyFile /run/secrets/realtime_membership_ticket_key",
        "add_env Services__Realtime__PublicSocketUrl wss://app.jeeb.fds-1.com/socket/websocket",
        "add_env Services__Realtime__BaseUrl http://jeeb-staging-realtime-comunication-service:4000",
        "add_env Services__ServiceOTP__BaseUrl http://jeeb-staging-one-time-password:8080",
        "add_env ServiceOTPApi__BaseUrl http://jeeb-staging-one-time-password:8080",
        "add_env Auth__Otp__Phone__AllowedRegion LB",
        "add_env Auth__Otp__Phone__EnforceRegion false",
        "add_env Auth__Otp__ApplicationId 0d51afe1-499f-4a29-a55a-36d2dd223b05",
        'add_rotated_secret "$probe_secret_name" staging_wss_probe_mint_key',
        'File:{Name:$target,UID:"65532",GID:"65532",Mode:256}',
        "add_env ASPNETCORE_ENVIRONMENT Staging",
    ),
)
if "add_env Operations__RealtimeProbe__MintKey " in documents["workflow"]:
    raise SystemExit("FAIL: staging workflow puts the probe mint key in the service environment")

require(
    "Spec transaction",
    (
        "submitted-pending-reconciliation",
        "unknown-third-preserved",
        "candidate-capture-failed-after-submit",
        'mv -f -- "$temporary" "$destination"',
    ),
)
require(
    "authenticated probe",
    (
        'EXACT_SOCKET_URL = f"wss://{HOST}/socket/websocket"',
        '"Upgrade: websocket\\r\\n"',
        '"Sec-WebSocket-Version: 13\\r\\n"',
        'response_head.decode("iso-8859-1")',
        "SAFE_DIAGNOSTIC_HEADERS = frozenset(",
        "def describe_upgrade_failure(status_line: str, header_lines: list[str]) -> str:",
        "raise RuntimeError(describe_upgrade_failure(lines[0], lines[1:]))",
        'response_headers.get("x-jeeb-realtime-proxy") != "gateway"',
        'WebSocket upgrade did not traverse the gateway proxy',
        'hashlib.sha1((websocket_key + WS_GUID)',
        '[reference, reference, topic, "phx_join", {"ticket": ticket}]',
        'event == "phx_reply"',
        'websocket = PhoenixWebSocket(EXACT_SOCKET_URL, token)',
        '{"reason": "forbidden"}',
        '{"reason": "not_in_membership"}',
        '{"conversation_id": conversation_id, "role": "client"}',
        'if actual["response"] != expected_response:',
        'if replay_status != 409:',
        'print("staging_authenticated_realtime_contract=ok")',
    ),
)
authenticated_probe = documents["authenticated probe"]
if authenticated_probe.count("PhoenixWebSocket(EXACT_SOCKET_URL, token)") != 1:
    raise SystemExit("FAIL: authenticated probe must use exactly one WebSocket connection")
single_connection = authenticated_probe.index(
    "websocket = PhoenixWebSocket(EXACT_SOCKET_URL, token)"
)
cross_topic_denial = authenticated_probe.index(
    '{"reason": "forbidden"}', single_connection
)
forged_ticket_denial = authenticated_probe.index(
    '{"reason": "not_in_membership"}', cross_topic_denial
)
exact_join = authenticated_probe.index(
    '{"conversation_id": conversation_id, "role": "client"}',
    forged_ticket_denial,
)
if not single_connection < cross_topic_denial < forged_ticket_denial < exact_join:
    raise SystemExit("FAIL: authenticated probe join sequence drifted")

require(
    "untrusted XFF probe",
    (
        "X-Forwarded-For: $spoofed_remote_ip",
        "x-jeeb-staging-observed-remote-ip",
        "ipaddress.ip_address(sys.argv[1])",
        "if observed == spoofed:",
    ),
)

require(
    "candidate contract",
    (
        'and ($networks == [{Target:$network_id}])',
        'http://jeeb-staging-one-time-password:8080',
        'http://jeeb-staging-realtime-comunication-service:4000',
        '0d51afe1-499f-4a29-a55a-36d2dd223b05',
        '$environment["auth__otp__phone__allowedregion"] == "LB"',
        '$environment["auth__otp__phone__enforceregion"] == "false"',
        '$environment["featureflags__useupstream__voice"] == "false"',
        '$environment["features__realtimewebsocketproxy__enabled"] == "false"',
        'def canonical_configuration_key: ascii_downcase | gsub("__"; ":")',
        'or startswith("forwardedheaders:knownproxies:")',
        'or startswith("forwardedheaders:knownnetworks:")',
        'and ($pairs | all((.key | forwarded_trust_key) | not))',
        '$environment["features__devendpoints__enabled"] == "true"',
        '$environment["features__swagger__enabled"] == "true"',
        '.UpdateConfig.Order == "start-first"',
        '.UpdateConfig.FailureAction == "pause"',
        '.RollbackConfig.Order == "start-first"',
        '.RollbackConfig.FailureAction == "pause"',
        'def canonical_identifier:',
        'def banned_legacy_host:',
        'def raw_secret_config_key:',
        'def embeds_inline_credential:',
        'def forbidden_payment_gateway_reference:',
        'contains(banned_legacy_host)',
        'unified[-_. ]*payment',
        'payment[-_. ]*gateway',
        '|upg)',
        'contains("192.168.2.20:10037")',
        'contains("192.168.2.20:10069")',
    ),
)


def validate_bootstrap_workflow(text):
    required = (
        "Require supported protected staging mode",
        "::error::Unsupported protected staging deployment mode.",
        "if: ${{ inputs.provider_expand_verified != true }}",
        "Require designated staging owner",
        '[ "$GITHUB_REF_PROTECTED" = true ]',
        '[ "$(hostname -s)" = "olivium-ephemerals" ]',
        'grep -Fxc "192.168.2.20"',
        "add_env FeatureFlags__UseUpstream__Chat false",
        "add_env FeatureFlags__UseUpstream__Realtime false",
        "add_env Features__RealtimeWebSocketProxy__Enabled false",
        "add_env FeatureFlags__UseUpstream__Voice false",
        "add_env FeatureFlags__UseUpstream__Otp true",
        "add_env Services__ServiceOTP__BaseUrl http://jeeb-staging-one-time-password:8080",
        "add_env ServiceOTPApi__BaseUrl http://jeeb-staging-one-time-password:8080",
        "add_env Auth__Otp__Phone__AllowedRegion LB",
        "add_env Auth__Otp__Phone__EnforceRegion false",
        "add_env Auth__Otp__ApplicationId 0d51afe1-499f-4a29-a55a-36d2dd223b05",
        "add_env Services__Realtime__BaseUrl http://jeeb-staging-realtime-comunication-service:4000",
        # Owner ruling 2026-08-27: the dev APIs — Super Login Plus among them —
        # must be available on STAGING (the Dev Tool ships to staging, never to
        # production). These two markers stay PINNED rather than being deleted:
        # the invariant they enforce is "staging sets these explicitly, never by
        # drift or default", and that still holds. Only the pinned value moves.
        # Flipping either back to false is a one-line change here plus the three
        # sites in jeeb-staging-deploy.yml / deploy/staging-gateway/*.env.
        "add_env SuperLogin__OpenMode true",
        "add_env DemoUsers__Enabled true",
        "add_env Features__DevEndpoints__Enabled true",
        "add_env Features__Swagger__Enabled true",
        "capture_remote_spec() {",
        "docker service inspect '$service' --format '{{json .Spec}}'",
        "docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'",
        'chmod 600 "$snapshot"',
        'staging_gateway_canonicalize_spec_file "$snapshot"',
        'staging_gateway_specs_equal "$pre_update_spec" "$incumbent_spec"',
        'cmp -s "$pre_update_version" "$incumbent_version"',
        'cmp -s "$pre_update_id" "$incumbent_id"',
        "verify_exact_candidate_after_checks() {",
        'capture_remote_spec "$final_spec" "$final_version" "$final_id"',
        'scripts/staging-gateway-terminal-candidate-check.sh',
        '"$DEPLOYMENT_MODE"',
        "write_snapshot_manifest() {",
        '"$incumbent_spec" "$incumbent_version" "$incumbent_id" "$incumbent_manifest"',
        'SecretNames: ([',
        "ServiceID: $id",
        "VersionIndex: $version",
        "ImageDigest: $digest",
        "Ports: ($spec[0].EndpointSpec.Ports // [])",
        "Networks: ($spec[0].TaskTemplate.Networks // [])",
        "Replicas: $spec[0].Mode.Replicated.Replicas",
        'FailureAction:"pause",Order:"start-first"',
        '"${published}:${target}:ingress"',
        "source scripts/staging-gateway-mutation-lock.sh",
        "source scripts/staging-gateway-spec-recovery.sh",
        "source scripts/staging-gateway-security-cutover.sh",
        "scripts/staging-gateway-spec-canonicalization.sh",
        "staging_gateway_external_gate_recover",
        "staging_gateway_forward_apply",
        "staging_gateway_security_cutover_forward_apply",
        "if (\n                  set -euo pipefail\n                  staging_gateway_external_gate_recover",
        "staging_gateway_submit_spec_cas() {",
        "registryAuthFrom=previous-spec",
        'EXPECTED_INCUMBENT_SPEC_SHA=$(sha256sum "$incumbent_spec"',
        'if length == 1 and (.[0] | type == "object") then .[0]',
        'recovery_result=armed-pending',
        'append_sanitized_transaction_summary',
        'if ! append_sanitized_transaction_summary; then',
        '[ "$status" -ne 0 ] || status=99',
        "always() && steps.remote_ghcr_login.outcome != 'skipped'",
        r'[ ! -L \"\$credential_dir\" ] || exit 98',
        r'[ ! -e \"\$credential_dir\" ] || [ -d \"\$credential_dir\" ] || exit 98',
        'scripts/staging-gateway-transaction-summary.sh',
        'scripts/staging-gateway-readiness-backoff.sh',
        'scripts/staging-gateway-public-edge-backoff.sh',
        'staging_gateway_lock_init jeeb-staging "$secret_stage"',
        "staging_gateway_lock_acquire",
        "staging_gateway_lock_assert",
        "staging_gateway_lock_release",
        'tolower($1) == tolower(expected)',
        "matches == 1 && exact_value == 1",
        "verify_bootstrap_flags",
        "probe_staging_authenticated_realtime",
        "python3 scripts/probe-staging-authenticated-realtime.py",
        "staging_realtime_ws_proxy_activated() {",
        "if staging_realtime_ws_proxy_activated; then",
        'startswith("features__realtimewebsocketproxy__enabled=")',
        "staging phase=authenticated-realtime result=skipped-proxy-inactive (redacted)",
        "probe_staging_untrusted_xff_contract",
        "staging phase=untrusted-xff-contract result=passed (redacted)",
        "staging phase=authenticated-realtime result=passed (redacted)",
        "verify_staging_overlay_and_dns",
        "jeeb-staging-one-time-password",
        "jeeb-staging-realtime-comunication-service",
        'select(.Options | has("encrypted"))',
        'select(.Options.encrypted == "" or .Options.encrypted == "true")',
        "-f scripts/staging-gateway-candidate-contract.jq",
        "scripts/verify-staging-otp-verify-freeze.sh",
        "inputs.deployment_mode != 'devtool-reassert'",
        "scripts/staging-gateway-devtool-reassert-candidate.jq",
        "scripts/staging-gateway-incumbent-devtool-posture.jq",
        "posture_mode=posture",
        "posture_mode=devtool-posture",
        "scripts/staging-gateway-public-edge-backoff.sh \\",
        'scripts/probe-staging-public-gateway-contract.sh "$posture_mode"',
        '|| return 1',
        "scripts/staging-gateway-public-edge-backoff.sh",
        "scripts/test-super-login.sh https://app.jeeb.fds-1.com",
    )
    missing = [marker for marker in required if marker not in text]
    if missing:
        raise ValueError(f"missing ingress-safe bootstrap/recovery markers: {missing}")

    forbidden = (
        "docker service " + "rollback",
        "&rollback=" + "previous",
        "docker service " + "create",
        '"${published}:${target}:host"',
        "docker service update --detach=false",
    )
    present = [marker for marker in forbidden if marker in text]
    if present:
        raise ValueError(f"unsafe staging mutation behavior remains: {present}")
    if 'append_sanitized_transaction_summary || status=99' in text:
        raise ValueError("summary failure overwrites the original deploy status")
    if text.count('FailureAction:"pause",Order:"start-first"') != 4:
        raise ValueError("every staging update and correction policy must be start-first and pause-on-failure")

    dispatch_header = text[: text.index("permissions:")]
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
        raise ValueError("staging deployment-mode dispatch contract drifted")
    input_references = set(
        re.findall(r"\binputs\.([A-Za-z][A-Za-z0-9_]*)", text)
    )
    if input_references != {"deployment_mode", "provider_expand_verified"}:
        raise ValueError(f"unexpected staging callable inputs: {sorted(input_references)}")

    secret_name_gate = text.index('if [ "$DEPLOYMENT_MODE" != devtool-reassert ]; then', text.index('service=jeeb-staging-jeeb-gateway'))
    secret_name_end = text.index('            secret_stage=$(mktemp -d)', secret_name_gate)
    secret_name_block = text[secret_name_gate:secret_name_end]
    for secret_name in (
        'state_secret_name="jeeb_staging_gateway_state_token_',
        'jwt_secret_name="jeeb_staging_gateway_jwt_',
        'probe_secret_name="jeeb_staging_gateway_wss_probe_',
    ):
        if secret_name not in secret_name_block:
            raise ValueError(f"run-scoped secret escaped the non-devtool gate: {secret_name}")
    stream_gate = text.index('if [ "$DEPLOYMENT_MODE" != devtool-reassert ]; then', text.index('staging_gateway_lock_acquire'))
    stream_end = text.index('            unset JEEB_STATE_SERVICE_TOKEN', stream_gate)
    stream_block = text[stream_gate:stream_end]
    if 'stream_secret "$state_secret_name"' not in stream_block:
        raise ValueError("run-scoped secret creation escaped the non-devtool gate")
    firebase_validation = text.index(
        'python3 scripts/validate-firebase-service-account.py',
        text.index('service=jeeb-staging-jeeb-gateway'),
    )
    firebase_name = text.index(
        'firebase_secret_name=$(bash scripts/firebase-docker-secret-name.sh',
        firebase_validation,
    )
    firebase_stream = text.index(
        'stream_content_addressed_secret_file "$firebase_secret_name" "$firebase_file"',
        firebase_name,
    )
    if not firebase_validation < firebase_name < firebase_stream < stream_gate:
        raise ValueError("Firebase validation/content-addressed rotation does not cover devtool-reassert")
    exact_devtool_builder = text.index('if [ "$DEPLOYMENT_MODE" = devtool-reassert ]; then', text.index('desired_env_json='))
    generic_builder = text.index('--slurpfile desired_env "$desired_env_json"', exact_devtool_builder)
    exact_delta = text.index('-f scripts/staging-gateway-devtool-reassert-candidate.jq', generic_builder)
    if not exact_devtool_builder < generic_builder < exact_delta:
        raise ValueError("Dev Tool candidate is not split from and checked after the generic builder")
    for authority in ("Chat", "Realtime", "Voice"):
        false_lock = f"add_env FeatureFlags__UseUpstream__{authority} false"
        true_lock = f"add_env FeatureFlags__UseUpstream__{authority} true"
        if text.count(false_lock) != 1 or true_lock in text:
            raise ValueError(f"staging bootstrap authority drifted: {authority}")

    mode_gate = text.index("Require supported protected staging mode")
    provider_gate = text.index("Hold caller activation until relay expand is verified")
    owner_gate = text.index("Require designated staging owner")
    first_external_mutation = min(
        text.index("docker/login-action@", mode_gate),
        text.index("docker/build-push-action@", mode_gate),
        text.index("ssh jeeb-staging", mode_gate),
        text.index("/services/$service_id/update?version=$expected_version", mode_gate),
    )
    if not mode_gate < provider_gate < owner_gate < first_external_mutation:
        raise ValueError("mode, provider, and owner gates do not precede every external mutation")
    if "if: always()" in text:
        raise ValueError("an always() step can bypass the protected staging gates")
    host_assertion = text.index("Assert exact staging host")
    topology_preflight = text.index("Preflight canonical Swarm ingress topology")
    registry_login = text.index("docker/login-action@")
    image_build = text.index("docker/build-push-action@")
    if not host_assertion < topology_preflight < registry_login < image_build:
        raise ValueError("target/topology assertions do not precede registry mutation")

    checkout = text.index("actions/checkout@")
    first_freeze = text.index("bash scripts/verify-staging-otp-verify-freeze.sh", checkout)
    first_ssh = text.index("Install cloudflared and configure strict SSH", first_freeze)
    if not checkout < first_freeze < first_ssh:
        raise ValueError("security-cutover freeze is not the first post-checkout deploy gate")
    if text.count("bash scripts/verify-staging-otp-verify-freeze.sh") != 3:
        raise ValueError("security-cutover must prove the exact freeze at three boundaries")

    pre_update = text.index(
        'capture_remote_spec "$pre_update_spec" "$pre_update_version" "$pre_update_id"'
    )
    candidate = text.index(
        'staging_gateway_canonicalize_spec_file "$candidate_raw_spec" "$candidate_spec"'
    )
    candidate_validation = text.index(
        '[ "$(jq -er \'.TaskTemplate.ContainerSpec.Image\' "$candidate_spec")" = "$IMAGE" ]',
        candidate,
    )
    task_capture = text.index("--execute capture", candidate_validation)
    pre_cas_freeze = text.index(
        "bash scripts/verify-staging-otp-verify-freeze.sh", task_capture
    )
    cutover_forward = text.index(
        "staging_gateway_security_cutover_forward_apply \\", pre_cas_freeze
    )
    arm = text.index("recovery_armed=true", cutover_forward)
    forward = text.index("staging_gateway_forward_apply \\", arm)
    manifest = text.index(
        '"$candidate_spec" "$candidate_version" "$candidate_id" "$candidate_manifest"',
        forward,
    )
    verifier = text.index("scripts/verify-swarm-service-image.sh", manifest)
    readiness = text.index("verify_candidate_readiness", verifier)
    false_flags = text.index("          verify_bootstrap_flags\n", readiness)
    public_probe = text.index(
        "bash scripts/staging-gateway-public-edge-backoff.sh", verifier
    )
    network = text.index("verify_staging_overlay_and_dns", public_probe)
    proxy_probe = text.index("probe_staging_untrusted_xff_contract", public_probe)
    descriptor_probe = text.index("probe_staging_authenticated_realtime", proxy_probe)
    final_candidate = text.index(
        "verify_exact_candidate_after_checks", descriptor_probe
    )
    old_task_proof = text.index("--execute verify", final_candidate)
    post_freeze = text.index(
        "bash scripts/verify-staging-otp-verify-freeze.sh", old_task_proof
    )
    final_confirm = text.index("verify_exact_candidate_after_checks", post_freeze)
    disarm = text.index("recovery_armed=false", final_confirm)
    if not pre_update < candidate < candidate_validation < task_capture < pre_cas_freeze < cutover_forward < arm < forward < manifest < verifier < readiness < false_flags < public_probe < network < proxy_probe < descriptor_probe < final_candidate < old_task_proof < post_freeze < final_confirm < disarm:
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
        "supported-mode gate removed",
        workflow.replace("Require supported protected staging mode", "Promotion gate", 1),
    ),
    (
        "protected-ref assertion removed",
        workflow.replace('[ "$GITHUB_REF_PROTECTED" = true ]', ":", 1),
    ),
    (
        "exact staging host changed",
        workflow.replace('hostname -s)" = "olivium-ephemerals"', 'hostname -s)" = "other-host"', 1),
    ),
    (
        "exact staging address changed",
        workflow.replace('grep -Fxc "192.168.2.20"', 'grep -Fxc "192.168.2.21"', 1),
    ),
    (
        "automatic rollback reintroduced",
        workflow.replace(
            'FailureAction:"pause",Order:"start-first"',
            'FailureAction:"rollback",Order:"start-first"',
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
        "WebSocket proxy bootstrap activated",
        workflow.replace(
            "add_env Features__RealtimeWebSocketProxy__Enabled false",
            "add_env Features__RealtimeWebSocketProxy__Enabled true",
            1,
        ),
    ),
    (
        "voice bootstrap activated",
        workflow.replace(
            "add_env FeatureFlags__UseUpstream__Voice false",
            "add_env FeatureFlags__UseUpstream__Voice true",
            1,
        ),
    ),
    (
        "OTP overlay endpoint weakened to host port",
        workflow.replace(
            "add_env Services__ServiceOTP__BaseUrl http://jeeb-staging-one-time-password:8080",
            "add_env Services__ServiceOTP__BaseUrl http://192.168.2.20:10037",
            1,
        ),
    ),
    (
        "OTP compatibility endpoint removed",
        workflow.replace(
            "add_env ServiceOTPApi__BaseUrl http://jeeb-staging-one-time-password:8080",
            "",
            1,
        ),
    ),
    (
        "international phone eligibility disabled",
        workflow.replace("add_env Auth__Otp__Phone__EnforceRegion false", "add_env Auth__Otp__Phone__EnforceRegion true", 1),
    ),
    (
        "realtime overlay endpoint weakened to host port",
        workflow.replace(
            "add_env Services__Realtime__BaseUrl http://jeeb-staging-realtime-comunication-service:4000",
            "add_env Services__Realtime__BaseUrl http://192.168.2.20:10069",
            1,
        ),
    ),
    (
        "encrypted overlay proof removed",
        workflow.replace('select(.Options | has("encrypted"))', "select(true)"),
    ),
    (
        "authenticated WSS probe removed",
        workflow.replace("python3 scripts/probe-staging-authenticated-realtime.py", ":", 1),
    ),
    (
        "WSS activation gate forced open",
        workflow.replace("if staging_realtime_ws_proxy_activated; then", "if true; then", 1),
    ),
    (
        "WSS activation gate reads workflow text instead of the deployed Spec",
        workflow.replace(
            'startswith("features__realtimewebsocketproxy__enabled=")', "true", 1
        ),
    ),
    (
        "WSS activation skip made silent",
        workflow.replace(
            "staging phase=authenticated-realtime result=skipped-proxy-inactive (redacted)",
            "",
            1,
        ),
    ),
    (
        "untrusted XFF evidence probe removed",
        workflow.replace(
            "            probe_staging_untrusted_xff_contract\n",
            "            :\n",
            1,
        ),
    ),
    (
        "canonical ingress preflight weakened to host mode",
        workflow.replace("${published}:${target}:ingress", "${published}:${target}:host"),
    ),
    (
        "external recovery source removed",
        workflow.replace("source scripts/staging-gateway-spec-recovery.sh", ":", 1),
    ),
    (
        "external recovery strict shell removed",
        workflow.replace(
            "if (\n                  set -euo pipefail\n                  staging_gateway_external_gate_recover",
            "if (\n                  set +e\n                  staging_gateway_external_gate_recover",
            1,
        ),
    ),
    (
        "incumbent manifest capture removed",
        workflow.replace(
            '"$incumbent_spec" "$incumbent_version" "$incumbent_id" "$incumbent_manifest"',
            '"$incumbent_spec" "$incumbent_version" "$incumbent_id" /dev/null',
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
        "terminal candidate semantic helper removed",
        workflow.replace("              bash scripts/staging-gateway-terminal-candidate-check.sh \\\n", "              return 0\n", 1),
    ),
    (
        "case-insensitive duplicate bootstrap flag guard removed",
        workflow.replace("matches == 1 && exact_value == 1", "exact_value >= 1", 1),
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
        'ObservedRemoteIpHeader =',
        '"X-Jeeb-Staging-Observed-Remote-Ip"',
        "context.Connection.RemoteIpAddress?.ToString()",
    ),
)
require("API-key middleware", ("StagingRealtimeProbeEndpoint.Route",))

contract = json.loads(contract_path.read_text())
if contract["openapi"] != "3.1.0":
    raise SystemExit("FAIL: producer contract must remain OpenAPI 3.1.0")
path = "/internal/ops/staging/realtime-probe-descriptor"
operation = contract["paths"][path]["post"]
success_headers = set(operation["responses"]["200"]["headers"])
if success_headers != {"Cache-Control", "X-Jeeb-Staging-Observed-Remote-Ip"}:
    raise SystemExit("FAIL: producer OpenAPI success evidence headers drifted")
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

print("Staging realtime probe, fail-visible block, ingress topology, and exact recovery contracts are exact.")
PY

python3 scripts/test-staging-authenticated-realtime-probe.py
bash scripts/test-staging-gateway-mutation-lock.sh
bash scripts/test-staging-gateway-spec-canonicalization.sh
bash scripts/test-staging-gateway-spec-recovery.sh
bash scripts/test-staging-gateway-security-cutover.sh
bash scripts/test-staging-gateway-candidate-contract.sh
bash scripts/test-staging-gateway-incumbent-devtool-posture.sh
bash scripts/test-staging-gateway-readiness-backoff.sh
bash scripts/test-staging-gateway-public-edge-backoff.sh
bash scripts/test-staging-gateway-terminal-candidate-check.sh
bash scripts/test-staging-public-gateway-probe-diagnostics.sh
bash scripts/test-staging-gateway-transaction-summary.sh
bash scripts/test-super-login-redaction-contract.sh
bash scripts/test-verify-staging-otp-verify-freeze.sh
bash scripts/test-probe-staging-untrusted-xff.sh
bash scripts/check-staging-gateway-phase-contracts.sh
bash scripts/test-assert-distinct-staging-signing-keys.sh
