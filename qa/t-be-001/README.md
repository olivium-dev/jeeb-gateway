# QA — T-BE-001 (Schemathesis + Pact + curl smoke)

QA-PRE scaffolding for **JEB-467**, the test-scenario subtask of Story **JEB-37 / T-BE-001** (OTP sign-in via `one-time-password` + `user-management`).

## What's in this directory

| File | Purpose |
|---|---|
| `ac-mapping.md` | Verbatim AC text → test method table (AC1..AC9 + AC-PhoneNorm + AC-GatewayRateLimit + AC-PhonePIIHash + AC-ProblemTypeSet). |
| `openapi-fragment.yaml` | OpenAPI 3.0 fragment for the two OTP endpoints. Schemathesis target. |
| `schemathesis.config.yaml` | Hypothesis settings, check list, endpoint-specific overrides. |
| `schemathesis-runner.sh` | Bash runner — starts a Docker container of the auth-service, runs Schemathesis with `--checks all --hypothesis-max-examples=200 --hypothesis-deadline=2000`, captures JUnit + cassette. |

## What's NOT in this directory

| Out-of-scope here | Owner |
|---|---|
| Pact consumer contract (T-MOB-004 ↔ auth-service) | Mobile QA — to be paired with this scaffolding when T-MOB-004 ships. |
| curl smoke harness | Owned by `principal-qa-api-curl`; sequenced after the auth-service container builds. |
| p99 perf assertion (AC7) | QA-POST (JEB-469). |
| SMS-arrival-within-30s (AC1 staging fence) | QA-POST (JEB-469) — requires real Twilio sandbox. |
| BOLA + fuzz validate | QA-POST (JEB-469). |

## How to run (once auth-service exists at jeeb-code/auth-service)

```bash
# 1) Build the auth-service Docker image at the repo root:
cd jeeb-code/auth-service
docker build -t olivium/auth-service:qa-pre .

# 2) Run Schemathesis:
cd qa/t-be-001
./schemathesis-runner.sh

# Narrow to a single endpoint:
./schemathesis-runner.sh --endpoint verify

# Skip the docker container (e.g. CI already has a target):
TARGET_URL=http://localhost:8080 ./schemathesis-runner.sh --no-docker
```

## What's mocked / what's real

| Dependency | Mocked? | Where |
|---|---|---|
| `olivium-dev/one-time-password` (Twilio-backed) | YES — NSubstitute-backed stateful fake | `AuthService.Tests/Otp/Fixtures/FakeOneTimePasswordClient.cs` |
| `olivium-dev/user-management` (phone-identity/find-or-create) | YES — NSubstitute fake mirroring JEB-1422 contract | `AuthService.Tests/Otp/Fixtures/FakeUserManagementClient.cs` |
| Postgres (refresh-token family persistence) | NO — real container via Testcontainers.PostgreSql 3.10 | `AuthService.Tests/Otp/Fixtures/TestcontainersFixture.cs` |
| Firebase | n/a — out of scope (audit comment #14764) |
| Twilio | n/a — never called from the test pipeline |
| `TimeProvider` | YES — `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` |

**Hard rule confirmation**: NO real Firebase, Twilio, `one-time-password`, or `user-management` call is issued by any test in this scaffolding. The boundary is the typed NSwag client; the fake reproduces downstream invariants documented in audit comment #14769 (3-attempt cap, 60s idempotency, 300s TTL).

## Coverage target

- Lines ≥ 85% on `AuthService.Otp.*`
- Branches ≥ 80% on `AuthService.Otp.*`

Threshold is **documented** here but **not enforced** in the scaffolding — gating belongs in the auth-service CI workflow (Tech-Lead JEB-465 / cicd lead seat).

## Audit reconciliation log

When the orchestrator prompt and audit comment #14769 disagreed, audit won (per the explicit hard rule "audit #14769 wins"). The differences:

| Topic | Prompt said | Audit says | Test asserts |
|---|---|---|---|
| Gateway rate limit | "5 OTP requests / 5 min per phone hash → 6th 429" | "10 req/min/IP AND 3 req/min/phone → 429" | 3/min/phone + 10/min/IP (audit) |
| Cap-reached status code | "423 Locked OR whatever AC-ProblemTypeSet specifies" | AC4 + AC-ProblemTypeSet: **429** with type=too_many_attempts | 429 |
| TTL-expiry response | "410 Gone with type=`urn:jeeb:otp:expired`" | AC-ProblemTypeSet has NO `expired` type; downstream collapses expired → invalid | 401 + type=invalid_otp |
