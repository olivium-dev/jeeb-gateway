#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
import json
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
