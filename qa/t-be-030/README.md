# T-BE-030 / JEB-66 — Cancellation Rules + Strikes (jeeb-gateway)

## What this story ships

`POST /v1/deliveries/{id}/cancel` — new gateway endpoint enforcing the
v1 cancellation policy from Q-OPEN-2 / T-PO-002. **Additive to** the
existing `POST /deliveries/{id}/cancel` (T-backend-024) surface; nothing
about that endpoint changes (AC5).

| Concern | Default | Source of truth |
|---|---|---|
| Client soft-limit | 3 / ISO week | `CancellationPolicyOptions.ClientSoftLimitPerWeek` |
| Client hard-limit | 5 / ISO week | `CancellationPolicyOptions.ClientHardLimitPerWeek` |
| Client cancellation fee | 15 000 LBP | `CancellationPolicyOptions.ClientCancellationFeeLbp` |
| Jeeber strike threshold | 3 / 30 days | `CancellationPolicyOptions.JeeberStrikeThreshold` |
| Jeeber suspension duration | 7 days | `CancellationPolicyOptions.JeeberRoleSuspensionDuration` |

All five values are runtime-tunable from `appsettings.json`
(`CancellationPolicy:*`) so PO can rotate thresholds without a redeploy.

## AC mapping

| AC | Statement | Test |
|---|---|---|
| AC1 | 4th client cancel/week → 200 + feeApplied | `V1CancellationPolicyEndpointTests.AC1_Client_Fourth_Cancel_This_Week_Is_Allowed_With_Fee` |
| AC2 | 6th client cancel/week → 429 + retryAfter | `V1CancellationPolicyEndpointTests.AC2_Client_Sixth_Cancel_This_Week_Returns_429_With_RetryAfter` |
| AC2 | Hard-limit resets at next Monday 00:00 UTC | `V1CancellationPolicyEndpointTests.AC2_Hard_Limit_Resets_On_New_ISO_Week` |
| AC3 | Jeeber 3rd strike / 30d → 7-day suspension | `V1CancellationPolicyEndpointTests.AC3_Jeeber_Third_Strike_In_30_Days_Suspends_Role_For_7_Days` |
| AC3 | Strikes older than 30d are excluded | `V1CancellationPolicyEndpointTests.AC3_Strikes_Older_Than_30_Days_Do_Not_Trip_The_Threshold` |
| AC4 | `cancel.policy_applied` log line emitted | `qa/t-be-030/observability-grep.sh` + `V1CancellationPolicyEndpointTests.AC4_Cancellation_Log_Captures_Action_And_Fee` |
| AC5 | No existing service broken | Full xUnit suite (694 tests) passes |
| AC5 | V1 surface does not poison legacy stores | `V1CancellationPolicyEndpointTests.AC5_V1_Cancel_Does_Not_Touch_Legacy_JeeberRestrictionStore` |
| AC6 | Cancel for status > picked → 422 too_late_to_cancel | `V1CancellationPolicyEndpointTests.AC6_Cancel_After_Picked_Returns_422_Too_Late_To_Cancel` |

## Olivium services consumed

| Repo | Adapter (interface) | MVP impl | Production swap |
|---|---|---|---|
| `olivium-dev/unified_payment_gateway` | `IUnifiedPaymentGatewayCancellationClient` | `InMemoryUnifiedPaymentGatewayCancellationClient` | NSwag-generated `ServiceUnifiedPaymentGatewayClient` (`ServiceUnifiedPaymentGatewayApi:BaseUrl`) — extend additively in the C10 branch |
| `olivium-dev/user-management` | `IJeeberRoleSuspensionClient` | `InMemoryJeeberRoleSuspensionClient` | NSwag-generated `UserManagementClient.SuspendRoleAsync` |
| `olivium-dev/notification-service` | `IPushNotificationService` (existing) | reused as-is | NSwag-generated `ServiceNotificationClient.CreateNotificationAsync` |

The MVP in-memory implementations record every call so the integration
tests can assert the side-effect surface without spinning a real HTTP
mock.

## How to run

```bash
# unit + integration tests
dotnet test tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj \
    --filter "FullyQualifiedName~V1CancellationPolicy"

# observability assertion (AC4)
dotnet test tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj \
    --filter "FullyQualifiedName~V1CancellationPolicy" \
    --logger "console;verbosity=normal" 2>&1 | tee /tmp/jeb-66.log
./qa/t-be-030/observability-grep.sh /tmp/jeb-66.log
```

## DB migration

`db/migrations/0014_cancellation_log.sql` — additive Postgres schema
backing the future swap of `InMemoryCancellationLogStore` to a
Postgres-backed implementation. Idempotent; safe to re-apply.
