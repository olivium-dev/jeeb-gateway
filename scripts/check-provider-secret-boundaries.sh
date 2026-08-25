#!/usr/bin/env bash
# The gateway forwards voice bytes to voice-transcription-service. Provider
# credentials and provider HTTP clients belong to that owning service only.

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

python3 - <<'PY'
import re
import subprocess
from pathlib import Path


LIFECYCLE = Path(".github/scripts/jeeb-gateway-secret-lifecycle.sh")
DIRECT_DEPLOY = Path(".github/workflows/deploy-to-jeeb.yml")
STAGING_DEPLOY = Path(".github/workflows/jeeb-staging-deploy.yml")

provider_secret_names = (
    "OPENAI" + "_API_KEY",
    "OLIVIUM_OPEN" + "_AI_KEY",
)
gateway_key = "Whisper" + "__ApiKey"
section_key = "Whisper" + ":ApiKey"
provider_origin = "api." + "openai.com"
fake_switch = "Whisper" + "__FakeTranscribe"
required_scrubs = {*provider_secret_names, gateway_key, fake_switch}

retired_source_paths = (
    "src/JeebGateway/Whisper/WhisperClient.cs",
    "src/JeebGateway/Whisper/FakeWhisperClient.cs",
    "src/JeebGateway/Whisper/IWhisperClient.cs",
    "src/JeebGateway/Whisper/ITranscriptionService.cs",
    "src/JeebGateway/Whisper/ResilientTranscriptionService.cs",
    "src/JeebGateway/Whisper/WhisperOptions.cs",
    "src/JeebGateway/Whisper/WhisperHealthCheck.cs",
    "src/JeebGateway/Whisper/WhisperCircuitBreaker.cs",
    "src/JeebGateway/Whisper/IAudioStore.cs",
    "src/JeebGateway/Whisper/ITranscriptionFallbackQueue.cs",
    "src/JeebGateway/Whisper/IFallbackTranscriptionProvider.cs",
)

tracked_names = subprocess.check_output(
    ["git", "ls-files", "-co", "--exclude-standard", "-z"]
).split(b"\0")
tracked = []
for raw_name in tracked_names:
    if not raw_name:
        continue
    path = Path(raw_name.decode("utf-8", "surrogateescape"))
    if not path.exists():
        continue
    data = path.read_bytes()
    if b"\0" in data:
        continue
    try:
        tracked.append((path, data.decode("utf-8")))
    except UnicodeDecodeError:
        continue

violations = []
direct = DIRECT_DEPLOY.read_text()
staging = STAGING_DEPLOY.read_text()


def scrub_lines(path: Path, text: str, declaration: str) -> set[int]:
    match = re.search(
        rf"(?ms)^\s*{re.escape(declaration)}=\(\n(?P<body>.*?)^\s*\)\s*$",
        text,
    )
    if match is None:
        violations.append((path, 1, f"{declaration} scrub array is missing"))
        return set()
    body = match.group("body")
    first_line = text[:match.start("body")].count("\n") + 1
    entries = [(first_line + offset, line.strip()) for offset, line in enumerate(body.splitlines())]
    for required in required_scrubs:
        if sum(entry == required or entry == f"--env-rm {required}" for _, entry in entries) != 1:
            violations.append((path, first_line, f"must scrub {required!r} exactly once"))
    return {
        line_number
        for line_number, entry in entries
        if entry in required_scrubs or entry in {f"--env-rm {key}" for key in required_scrubs}
    }


direct_scrub_lines = scrub_lines(DIRECT_DEPLOY, direct, "retired_env_args")
staging_scrub_lines = scrub_lines(STAGING_DEPLOY, staging, "retired_gateway_env")


def is_allowed_scrub(path: Path, line_number: int, line: str, marker: str) -> bool:
    stripped = line.strip()
    if path == DIRECT_DEPLOY and line_number in direct_scrub_lines:
        return stripped == f"--env-rm {marker}"
    if path == STAGING_DEPLOY and line_number in staging_scrub_lines:
        return stripped == marker
    return False


for path, text in tracked:
    for line_number, line in enumerate(text.splitlines(), 1):
        folded_line = line.casefold()
        for marker in (*provider_secret_names, section_key, provider_origin):
            if marker.casefold() in folded_line and not is_allowed_scrub(path, line_number, line, marker):
                violations.append((path, line_number, f"forbidden provider marker {marker!r}"))
        if gateway_key.casefold() in folded_line:
            if not is_allowed_scrub(path, line_number, line, gateway_key):
                violations.append((path, line_number, f"gateway provider key {gateway_key!r}"))

source_provider = re.compile(
    r"\bopenai\b|\b(?:I?WhisperClient|WhisperOptions|WhisperHealthCheck|"
    r"WhisperCircuitBreaker|ITranscriptionService|IAudioStore|"
    r"ITranscriptionFallbackQueue|IFallbackTranscriptionProvider)\b|"
    r"^\s*\"Whisper\"\s*:",
    re.IGNORECASE,
)
if not source_provider.search("services.AddHttpClient<IWhisperClient, WhisperClient>()"):
    raise SystemExit("FAIL: retired provider-source matcher missed its adversarial canary")
for path, text in tracked:
    if not path.as_posix().startswith("src/JeebGateway/"):
        continue
    for match in source_provider.finditer(text):
        line_number = text.count("\n", 0, match.start()) + 1
        violations.append((path, line_number, "gateway-local provider source/config marker"))

for retired in retired_source_paths:
    if Path(retired).exists():
        violations.append((Path(retired), 1, "retired in-gateway transcription provider path exists"))

for retired_config in (gateway_key, fake_switch):
    injection = re.compile(
        r"--env(?:-add)?\s+" + re.escape(retired_config) + r"(?:=|\s)",
        re.IGNORECASE,
    )
    canary = "--env-add " + retired_config + "=${{ secrets.PROVIDER_KEY }}"
    if not injection.search(canary):
        raise SystemExit("FAIL: provider injection matcher missed its adversarial canary")
    if injection.search("--env-rm " + retired_config):
        raise SystemExit("FAIL: provider injection matcher rejects the required legacy-key scrub")
    if injection.search(direct):
        violations.append((DIRECT_DEPLOY, 1, f"gateway deploy injects retired config {retired_config!r}"))
if "whisper_" + "fake:" in direct.lower():
    violations.append((DIRECT_DEPLOY, 1, "gateway deploy exposes the retired local transcription switch"))
if "WHISPER" + "_FAKE_TRANSCRIBE" in direct:
    violations.append((DIRECT_DEPLOY, 1, "gateway deploy injects the retired flat fake-provider switch"))

voice_input = re.compile(
    r"^\s*voice_transcription_base_url:\s*\{[^}]*required:\s*true,\s*default:\s*''\s*\}",
    re.MULTILINE,
)
if not voice_input.search(direct):
    violations.append((DIRECT_DEPLOY, 1, "voice owner target must be an explicit blank-by-default input"))
if direct.count("voice_transcription_base_url=${{ inputs.voice_transcription_base_url }}") != 1:
    violations.append((DIRECT_DEPLOY, 1, "voice owner target is missing from the pre-build URL guard"))
voice_binding = "Services__VoiceTranscription__BaseUrl='${{ inputs.voice_transcription_base_url }}'"
if direct.count("--env-add " + voice_binding) != 1:
    violations.append((DIRECT_DEPLOY, 1, "voice owner target needs exactly one update binding"))
if "--env " + voice_binding in direct:
    violations.append((DIRECT_DEPLOY, 1, "update-only deploy reintroduced a create-time voice binding"))
voice_flag = "FeatureFlags__UseUpstream__Voice='true'"
if direct.count("--env-add " + voice_flag) != 1:
    violations.append((DIRECT_DEPLOY, 1, "voice owner route needs exactly one update enablement"))
if "--env " + voice_flag in direct:
    violations.append((DIRECT_DEPLOY, 1, "update-only deploy reintroduced a create-time voice enablement"))

for expected in (
    "add_env FeatureFlags__UseUpstream__Voice false",
    "add_env Services__VoiceTranscription__BaseUrl http://192.168.2.20:10062",
):
    if staging.count(expected) != 1:
        violations.append((STAGING_DEPLOY, 1, f"staging voice-owner contract drifted: {expected}"))

lifecycle = LIFECYCLE.read_text()
for required in ("OPENAI_*", "OLIVIUM_OPEN_*", "Whisper__Api[Kk]ey"):
    if required not in lifecycle:
        violations.append((LIFECYCLE, 1, f"runtime service-spec guard does not reject {required!r}"))

program = Path("src/JeebGateway/Program.cs").read_text()
if "IVoiceTranscriptionClient" not in Path(
    "src/JeebGateway/Extensions/ServiceClientExtensions.cs"
).read_text():
    violations.append((Path("src/JeebGateway/Extensions/ServiceClientExtensions.cs"), 1,
                       "owning voice-transcription-service client registration is missing"))
if "Whisper" + "Options" in program or "Whisper" + "Client" in program:
    violations.append((Path("src/JeebGateway/Program.cs"), 1,
                       "gateway-local transcription provider registration was reintroduced"))

if violations:
    lines = ["FAIL: gateway provider-secret boundary drifted:"]
    lines.extend(f"  {path}:{line_number}: {reason}" for path, line_number, reason in violations)
    raise SystemExit("\n".join(lines))

print(f"Audited {len(tracked)} repository UTF-8 files.")
print("Gateway has no provider secret/source path; legacy runtime keys are scrubbed fail-closed.")
PY
