#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
import json
import re
from pathlib import Path

workflow_path = Path(".github/workflows/jeeb-staging-deploy.yml")
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
        "stream_secret \"$probe_secret_name\" \"$JEEB_STAGING_WSS_PROBE_MINT_KEY\"",
        "add_env Operations__RealtimeProbe__MintKeyFile /run/secrets/staging_wss_probe_mint_key",
        "add_env Services__Realtime__GuardianSecretFile /run/secrets/realtime_guardian_secret",
        "add_env Services__Realtime__MembershipTicketSigningKeyFile /run/secrets/realtime_membership_ticket_key",
        "add_env Services__Realtime__PublicSocketUrl wss://app.jeeb.fds-1.com/socket/websocket",
        "add_rotated_secret \"$probe_secret_name\" staging_wss_probe_mint_key",
        'uid=65532,gid=65532,mode=0400',
        "add_env ASPNETCORE_ENVIRONMENT Staging",
    ),
)
if "add_env Operations__RealtimeProbe__MintKey " in documents["workflow"]:
    raise SystemExit("FAIL: staging workflow puts the probe mint key in the service environment")


def validate_bootstrap_workflow(text):
    required = (
        "add_env FeatureFlags__UseUpstream__Chat false",
        "add_env FeatureFlags__UseUpstream__Realtime false",
        "capture_remote_spec() {",
        "docker service inspect '$service' --format '{{json .Spec}}'",
        "docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'",
        'chmod 600 "$snapshot"',
        'cmp -s "$recovery_spec" "$incumbent_spec"',
        'cmp -s "$recovery_spec" "$candidate_spec"',
        'capture_remote_spec "$confirm_spec" "$confirm_version" "$confirm_id"',
        'cmp -s "$confirm_spec" "$candidate_spec"',
        'candidate_index=$(<"$candidate_version")',
        'confirm_index=$(<"$confirm_version")',
        'candidate_service_id=$(<"$candidate_id")',
        'confirm_service_id=$(<"$confirm_id")',
        'incumbent_service_id=$(<"$incumbent_id")',
        'recovery_service_id=$(<"$recovery_id")',
        '[ "$recovery_service_id" = "$incumbent_service_id" ]',
        '[ "$recovery_service_id" != "$candidate_service_id" ]',
        '[ "$candidate_service_id" != "$confirm_service_id" ]',
        "service_id='$candidate_service_id'",
        'if [ "$candidate_index" != "$confirm_index" ]',
        "update?version=\\${expected_version}&rollback=previous",
        "&registryAuthFrom=previous-spec",
        'case "$cas_status" in',
        "409)",
        "rollback CAS outcome is ambiguous and authoritative state is unavailable",
        "rollback CAS outcome did not reconcile to the exact incumbent",
        "RED: authoritative service Spec unavailable; recovery made no mutation",
        "RED: service Spec drifted before rollback; recovery made no mutation",
        "RED: service Spec is neither exact incumbent nor exact candidate; recovery made no mutation",
        'cmp -s "$restored_spec" "$incumbent_spec"',
        'cmp -s "$pre_update_version" "$incumbent_version"',
        'cmp -s "$pre_update_id" "$incumbent_id"',
        "verify_exact_candidate_before_disarm() {",
        'capture_remote_spec "$final_spec" "$final_version" "$final_id"',
        'cmp -s "$final_spec" "$candidate_spec"',
        'cmp -s "$final_version" "$candidate_version"',
        'cmp -s "$final_id" "$candidate_id"',
        'tolower($1) == tolower(expected)',
        'matches == 1 && exact_false == 1',
        'capture_remote_spec "$recovery_spec" "$recovery_version" "$recovery_id"',
        'capture_remote_spec "$restored_spec" "$restored_version" "$restored_id"',
        '< scripts/verify-swarm-service-image.sh',
        "sha256sum \"$incumbent_spec\"",
        "sha256sum \"$restored_spec\"",
        "verify_bootstrap_flags",
        "probe_staging_realtime_descriptor",
        'STAGING_REALTIME_PROBE_KEY_FILE="$probe_key_file" python3',
        'PATH = "/internal/ops/staging/realtime-probe-descriptor"',
        'if malformed_status != 400:',
        'if forged_status != 403:',
        'if status != 200:',
        'if replay_status != 409:',
        'if set(descriptor) != expected_fields:',
        'if not 30 <= ttl <= 900:',
        '"no-store" not in',
        'descriptor["conversationId"] != conversation_id',
        'descriptor["topic"] != "jeeb:chat:" + conversation_id',
        'descriptor["socketUrl"] != "wss://app.jeeb.fds-1.com/socket/websocket"',
    )
    missing = [marker for marker in required if marker not in text]
    if missing:
        raise ValueError(f"missing bootstrap/recovery markers: {missing}")

    dispatch_header = text[: text.index("permissions:")]
    if re.search(r"(?m)^\s+inputs:\s*$", dispatch_header) or "${{ inputs." in text:
        raise ValueError("staging bootstrap exposes a callable activation input")
    for authority in ("Chat", "Realtime"):
        false_lock = f"add_env FeatureFlags__UseUpstream__{authority} false"
        true_lock = f"add_env FeatureFlags__UseUpstream__{authority} true"
        if text.count(false_lock) != 1 or true_lock in text:
            raise ValueError(f"staging bootstrap authority drifted: {authority}")

    recovery_start = text.index("recover_exact_incumbent() {")
    recovery_end = text.index("verify_bootstrap_flags() {", recovery_start)
    recovery = text[recovery_start:recovery_end]
    rollback_command = "docker service " + "rollback"
    rollback_cas = "&rollback=" + "previous"
    if rollback_command in recovery or recovery.count(rollback_cas) != 1:
        raise ValueError("recovery must contain exactly one version-bound rollback CAS")
    if "service_id=\\$(docker service inspect" in recovery:
        raise ValueError("rollback CAS fresh-inspects a replaceable service identity")
    if recovery.count("scripts/verify-swarm-service-image.sh") != 1:
        raise ValueError("recovery must invoke the exact checked-in runtime verifier once")
    if recovery.count(
        'capture_remote_spec "$restored_spec" "$restored_version" "$restored_id"'
    ) != 2:
        raise ValueError("recovery must separately reconcile ambiguous and accepted CAS outcomes")
    unknown_branch = recovery.index(
        "RED: service Spec is neither exact incumbent nor exact candidate"
    )
    if rollback_cas in recovery[unknown_branch:]:
        raise ValueError("unknown recovery state can reach a rollback mutation")

    arm = text.index("rollback_armed=true")
    pre_update = text.index(
        'capture_remote_spec "$pre_update_spec" "$pre_update_version" "$pre_update_id"'
    )
    candidate = text.index(
        'capture_remote_spec "$candidate_spec" "$candidate_version"', arm
    )
    false_flags = text.index("verify_bootstrap_flags", candidate)
    verifier = text.index("scripts/verify-swarm-service-image.sh", false_flags)
    public_probe = text.index(
        "bash scripts/probe-staging-public-gateway-contract.sh", verifier
    )
    descriptor_probe = text.index("probe_staging_realtime_descriptor", public_probe)
    final_candidate = text.index(
        "verify_exact_candidate_before_disarm", descriptor_probe
    )
    disarms = [
        match.start()
        for match in re.finditer(r"(?m)^\s*rollback_armed=false\s*$", text)
        if match.start() > arm
    ]
    if len(disarms) != 1:
        raise ValueError("staging bootstrap must have exactly one post-arm disarm")
    if "rollback_armed=true\n          {" not in text:
        raise ValueError("recovery is not armed immediately before candidate mutation")
    if not pre_update < arm < candidate < false_flags < verifier < public_probe < descriptor_probe < final_candidate < disarms[0]:
        raise ValueError("staging bootstrap gates are not all inside the armed interval")


workflow = documents["workflow"]
validate_bootstrap_workflow(workflow)

negative_controls = (
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
        "candidate second-read removed",
        workflow.replace(
            'capture_remote_spec "$confirm_spec" "$confirm_version" "$confirm_id"',
            ":",
            1,
        ),
    ),
    (
        "candidate Service.ID binding removed",
        workflow.replace(
            '[ "$candidate_service_id" != "$confirm_service_id" ]',
            '[ "$candidate_service_id" != "$candidate_service_id" ]',
            1,
        ),
    ),
    (
        "rollback CAS auth source changed from previous Spec",
        workflow.replace("registryAuthFrom=previous-spec", "registryAuthFrom=spec", 1),
    ),
    (
        "rollback CAS reintroduced a fresh service-ID inspect",
        workflow.replace(
            "service_id='$candidate_service_id'",
            "service_id=\\$(docker service inspect '$service' --format '{{.ID}}')",
            1,
        ),
    ),
    (
        "ambiguous CAS reconciliation removed",
        workflow.replace(
            'capture_remote_spec "$restored_spec" "$restored_version" "$restored_id"',
            ":",
            1,
        ),
    ),
    (
        "exact incumbent runtime verifier removed",
        workflow.replace("< scripts/verify-swarm-service-image.sh", "< /dev/null", 1),
    ),
    (
        "recovery armed before final identity recheck",
        workflow.replace(
            '          EXPECTED_PREVIOUS_IMAGE=$previous_image\n'
            '          : "$EXPECTED_PREVIOUS_IMAGE"\n'
            "          rollback_armed=true",
            "          rollback_armed=true\n"
            "          EXPECTED_PREVIOUS_IMAGE=$previous_image\n"
            '          : "$EXPECTED_PREVIOUS_IMAGE"',
            1,
        ),
    ),
    (
        "rollback allowed on unknown state",
        workflow.replace(
            "echo 'RED: service Spec is neither exact incumbent nor exact candidate; recovery made no mutation' >&2",
            'curl "http://localhost/services/id/update?version=1&rollback=' + 'previous"',
            1,
        ),
    ),
    (
        "descriptor gate moved after disarm",
        workflow.replace(
            "          probe_staging_realtime_descriptor\n"
            "          verify_exact_candidate_before_disarm\n"
            "          rollback_armed=false",
            "          rollback_armed=false\n"
            "          probe_staging_realtime_descriptor\n"
            "          verify_exact_candidate_before_disarm",
            1,
        ),
    ),
    (
        "final candidate identity gate removed",
        workflow.replace(
            "          verify_exact_candidate_before_disarm\n",
            "",
            1,
        ),
    ),
    (
        "case-insensitive duplicate bootstrap flag guard removed",
        workflow.replace(
            "matches == 1 && exact_false == 1",
            "exact_false >= 1",
            1,
        ),
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
require(
    "API-key middleware",
    ("StagingRealtimeProbeEndpoint.Route",),
)

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

print("Staging realtime probe HMAC, replay, descriptor, and deploy contracts are exact.")
PY

recovery_functions=$(mktemp)
recovery_harness_root=$(mktemp -d)
cleanup_recovery_harness() {
  rm -f -- "$recovery_functions"
  rm -rf -- "$recovery_harness_root"
}
trap cleanup_recovery_harness EXIT

python3 - <<'PY' > "$recovery_functions"
from pathlib import Path

workflow = Path(".github/workflows/jeeb-staging-deploy.yml").read_text()
start_marker = "            capture_remote_spec() {"
end_marker = "            verify_bootstrap_flags() {"
start = workflow.index(start_marker)
end = workflow.index(end_marker, start)
for line in workflow[start:end].splitlines():
    if line.startswith("          "):
        line = line[10:]
    print(line)
PY
bash -n "$recovery_functions"

# The sourced production helpers consume these harness globals dynamically.
# shellcheck disable=SC2034
run_recovery_case() (
  set -euo pipefail
  scenario=$1
  expected_status=$2
  expected_cas=$3
  expected_mutations=$4
  expected_active=$5
  secret_stage=$(mktemp -d "$recovery_harness_root/${scenario}.XXXXXX")
  chmod 700 "$secret_stage"
  service=jeeb-staging-jeeb-gateway
  published=10000
  health=/health/ready
  previous_image="repo@sha256:$(printf 'a%.0s' $(seq 1 64))"

  incumbent_spec="$secret_stage/incumbent-service-spec.json"
  incumbent_version="$secret_stage/incumbent-service-version"
  incumbent_id="$secret_stage/incumbent-service-id"
  candidate_spec="$secret_stage/candidate-service-spec.json"
  candidate_version="$secret_stage/candidate-service-version"
  candidate_id="$secret_stage/candidate-service-id"
  recovery_spec="$secret_stage/recovery-service-spec.json"
  recovery_version="$secret_stage/recovery-service-version"
  recovery_id="$secret_stage/recovery-service-id"
  confirm_spec="$secret_stage/confirm-service-spec.json"
  confirm_version="$secret_stage/confirm-service-version"
  confirm_id="$secret_stage/confirm-service-id"
  restored_spec="$secret_stage/restored-service-spec.json"
  restored_version="$secret_stage/restored-service-version"
  restored_id="$secret_stage/restored-service-id"
  pre_update_spec="$secret_stage/pre-update-service-spec.json"
  pre_update_version="$secret_stage/pre-update-service-version"
  pre_update_id="$secret_stage/pre-update-service-id"
  final_spec="$secret_stage/final-service-spec.json"
  final_version="$secret_stage/final-service-version"
  final_id="$secret_stage/final-service-id"

  incumbent_fixture="$secret_stage/incumbent.json"
  candidate_fixture="$secret_stage/candidate.json"
  third_fixture="$secret_stage/third.json"
  candidate_image="repo@sha256:$(printf 'b%.0s' $(seq 1 64))"
  third_image="repo@sha256:$(printf 'c%.0s' $(seq 1 64))"
  printf '{"TaskTemplate":{"ContainerSpec":{"Image":"%s","Env":["Chat=false","Realtime=false","Secret=v1"]}}}\n' \
    "$previous_image" > "$incumbent_fixture"
  if [ "$scenario" = same_digest ]; then
    candidate_image=$previous_image
  fi
  printf '{"TaskTemplate":{"ContainerSpec":{"Image":"%s","Env":["Chat=false","Realtime=false","Secret=v2"]}}}\n' \
    "$candidate_image" > "$candidate_fixture"
  if [ "$scenario" = final_candidate_drift ]; then
    printf '{"TaskTemplate":{"ContainerSpec":{"Image":"%s","Env":["Chat=false","Realtime=false","Secret=concurrent-drift"]}}}\n' \
      "$candidate_image" > "$third_fixture"
  else
    printf '{"TaskTemplate":{"ContainerSpec":{"Image":"%s","Env":["Chat=true","Realtime=true","Secret=third"]}}}\n' \
      "$third_image" > "$third_fixture"
  fi
  chmod 600 "$incumbent_fixture" "$candidate_fixture" "$third_fixture"
  cp "$incumbent_fixture" "$incumbent_spec"
  cp "$candidate_fixture" "$candidate_spec"
  printf '%s\n' 100 > "$incumbent_version"
  printf '%s\n' 200 > "$candidate_version"
  printf '%s\n' serviceaaaaaaaa > "$incumbent_id"
  printf '%s\n' serviceaaaaaaaa > "$candidate_id"
  chmod 600 "$incumbent_spec" "$candidate_spec" "$incumbent_version" \
    "$candidate_version" "$incumbent_id" "$candidate_id"
  if [ "$scenario" = unknown_without_candidate ]; then
    rm -f -- "$candidate_spec" "$candidate_id"
  fi

  active_kind_file="$secret_stage/active-kind"
  active_id_file="$secret_stage/active-id"
  active_version_file="$secret_stage/active-version"
  cas_count_file="$secret_stage/cas-count"
  mutation_count_file="$secret_stage/mutation-count"
  snapshot_count_file="$secret_stage/snapshot-count"
  verifier_count_file="$secret_stage/verifier-count"
  public_count_file="$secret_stage/public-count"
  printf '%s\n' 0 > "$cas_count_file"
  printf '%s\n' 0 > "$mutation_count_file"
  printf '%s\n' 0 > "$snapshot_count_file"
  printf '%s\n' 0 > "$verifier_count_file"
  printf '%s\n' 0 > "$public_count_file"
  case "$scenario" in
    auto_rollback) printf '%s\n' incumbent > "$active_kind_file" ;;
    third_state|final_candidate_drift) printf '%s\n' third > "$active_kind_file" ;;
    unavailable) printf '%s\n' unavailable > "$active_kind_file" ;;
    *) printf '%s\n' candidate > "$active_kind_file" ;;
  esac
  case "$scenario" in
    service_id_replacement) printf '%s\n' servicebbbbbbbb > "$active_id_file" ;;
    *) printf '%s\n' serviceaaaaaaaa > "$active_id_file" ;;
  esac
  case "$(<"$active_kind_file")" in
    incumbent) printf '%s\n' 100 > "$active_version_file" ;;
    candidate) printf '%s\n' 200 > "$active_version_file" ;;
    *) printf '%s\n' 300 > "$active_version_file" ;;
  esac

  increment_file() {
    local counter_file=$1 value
    value=$(<"$counter_file")
    printf '%s\n' "$((value + 1))" > "$counter_file"
  }
  set_active() {
    printf '%s\n' "$1" > "$active_kind_file"
    printf '%s\n' "$2" > "$active_id_file"
    printf '%s\n' "$3" > "$active_version_file"
  }
  active_fixture() {
    case "$(<"$active_kind_file")" in
      incumbent) printf '%s\n' "$incumbent_fixture" ;;
      candidate) printf '%s\n' "$candidate_fixture" ;;
      third) printf '%s\n' "$third_fixture" ;;
      *) return 1 ;;
    esac
  }
  ssh() {
    [ "$1" = jeeb-staging ]
    shift
    local remote_command="$*" body_file snapshot_number
    if [[ "$remote_command" == *"{{.ID}} {{.Version.Index}}"* ]]; then
      [ "$(<"$active_kind_file")" != unavailable ] || return 1
      printf '%s %s\n' "$(<"$active_id_file")" "$(<"$active_version_file")"
      return 0
    fi
    if [[ "$remote_command" == *"{{json .Spec}}"* ]]; then
      [ "$(<"$active_kind_file")" != unavailable ] || return 1
      increment_file "$snapshot_count_file"
      snapshot_number=$(<"$snapshot_count_file")
      if [ "$scenario" = confirm_drift ] && [ "$snapshot_number" -eq 2 ]; then
        set_active third serviceaaaaaaaa 201
      fi
      cat "$(active_fixture)"
      return 0
    fi
    if [[ "$remote_command" == *"update?version="* ]]; then
      body_file="$secret_stage/cas-body.json"
      cat > "$body_file"
      chmod 600 "$body_file"
      cmp -s "$body_file" "$candidate_spec"
      [[ "$remote_command" == *"/services/serviceaaaaaaaa/update?version=200&rollback=previous&registryAuthFrom=previous-spec"* ]]
      increment_file "$cas_count_file"
      case "$scenario" in
        final_read_race)
          set_active third serviceaaaaaaaa 201
          printf '%s' 409
          ;;
        ambiguous_after_commit)
          increment_file "$mutation_count_file"
          set_active incumbent serviceaaaaaaaa 201
          return 1
          ;;
        rollback_failure)
          printf '%s' 500
          ;;
        *)
          increment_file "$mutation_count_file"
          set_active incumbent serviceaaaaaaaa 201
          printf '%s' 200
          ;;
      esac
      return 0
    fi
    if [ "${1:-}" = bash ] && [ "${2:-}" = -s ] && [ "${3:-}" = -- ]; then
      cat >/dev/null
      if [ "${4:-}" = "$service" ] && [ "${5:-}" = "$previous_image" ]; then
        increment_file "$verifier_count_file"
      fi
      return 0
    fi
    return 99
  }
  bash() {
    if [ "${1:-}" = scripts/probe-staging-public-gateway-contract.sh ]; then
      increment_file "$public_count_file"
      return 0
    fi
    command bash "$@"
  }

  # Source and execute the actual workflow helpers; fakes replace only external
  # SSH/Engine/public boundaries and can never produce a live deployment PASS.
  # shellcheck disable=SC1090
  source "$recovery_functions"
  if [ "$scenario" = final_candidate_drift ]; then
    set +e
    verify_exact_candidate_before_disarm >/dev/null 2>&1
    final_gate_status=$?
    recover_exact_incumbent >/dev/null 2>&1
    actual_status=$?
    set -e
    [ "$final_gate_status" -ne 0 ]
    [ "$actual_status" -eq "$expected_status" ]
    [ "$(<"$cas_count_file")" -eq "$expected_cas" ]
    [ "$(<"$mutation_count_file")" -eq "$expected_mutations" ]
    [ "$(<"$active_kind_file")" = "$expected_active" ]
    [ "$(<"$verifier_count_file")" -eq 0 ]
    [ "$(<"$public_count_file")" -eq 0 ]
    exit 0
  fi
  set +e
  recover_exact_incumbent >/dev/null 2>&1
  actual_status=$?
  set -e
  [ "$actual_status" -eq "$expected_status" ]
  [ "$(<"$cas_count_file")" -eq "$expected_cas" ]
  [ "$(<"$mutation_count_file")" -eq "$expected_mutations" ]
  [ "$(<"$active_kind_file")" = "$expected_active" ]
  if [ "$expected_status" -eq 0 ]; then
    [ "$(<"$verifier_count_file")" -eq 1 ]
    [ "$(<"$public_count_file")" -eq 1 ]
  else
    [ "$(<"$verifier_count_file")" -eq 0 ]
    [ "$(<"$public_count_file")" -eq 0 ]
  fi
)

run_recovery_case candidate 0 1 1 incumbent
run_recovery_case same_digest 0 1 1 incumbent
run_recovery_case auto_rollback 0 0 0 incumbent
run_recovery_case unknown_without_candidate 1 0 0 candidate
run_recovery_case third_state 1 0 0 third
run_recovery_case unavailable 1 0 0 unavailable
run_recovery_case confirm_drift 1 0 0 third
run_recovery_case final_read_race 1 1 0 third
run_recovery_case ambiguous_after_commit 0 1 1 incumbent
run_recovery_case rollback_failure 1 1 0 candidate
run_recovery_case service_id_replacement 1 0 0 candidate
run_recovery_case final_candidate_drift 1 0 0 third

echo "Actual recovery helper SSH/Engine adversarial harness PASSED"
