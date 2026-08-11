# Waiver — PR #373 merged with red CI

**Status:** ratified, retrospective
**Recorded:** 2026-08-11
**PR:** [#373 — fix(notifications): fail closed on gateway direct push dispatch](https://github.com/olivium-dev/jeeb-gateway/pull/373)
**Branch:** `fix/push-dispatch-fail-closed`
**Merge commit:** `907e07b88713f54de5463e1045ec107b15a931a7`
**Merged:** 2026-08-10T07:44:30Z
**Parent (`main` before the merge):** `601ea6af8785becf0eedda2f8ed8f7e0021cb4a5`

## What was waived

The standing rule is *CI green before merge*. #373 was merged while three
required checks were failing: `build`, `build-and-test`, `stateless-gate`.

## Why it was acceptable

The red was **pre-existing on `main`, not introduced by #373.**

Check conclusions on the parent commit `601ea6a`, on the #373 merge commit
`907e07b8`, and on current `main` `fb52a2d` are identical:

| Check | `601ea6a` (before) | `907e07b8` (#373) | `fb52a2d` (now) |
| --- | --- | --- | --- |
| `build` | failure | failure | failure |
| `build-and-test` | failure | failure | failure |
| `stateless-gate` | failure | failure | failure |
| `Database migrations` | success | success | success |
| `Gitleaks Secret Scan` | success | success | success |
| `nswag-freshness` | success | success | success |
| `nswag-otp-freshness` | success | success | success |

The failing **test list is byte-for-byte identical** on `601ea6a` and
`907e07b8` — 12 tests, same names, same order:

```
ChatControllerErrorShapeTests.ReplyToMessage_Is_Retired_Returns_410_Gone_Without_Upstream_Call
ChatServiceConversationProvisionerTests.Enabled_creates_member_first_then_channel_with_minted_member_id
ChatServiceConversationProvisionerTests.Enabled_degrades_to_null_when_member_create_returns_no_id
ClientVisibilityAndReceiptTests.GetById_AfterFullLifecycleToDone_StillCarriesAmountAndJeeberName_ForBothParties
ConversationBoundaryGuardTests.Create_Orchestration_Composes_Generic_Member_Then_Channel_Primitives_Only
PostgresRequestExpiryAuthorityTests.Concurrent_replica_sweeps_expire_pending_request_exactly_once
PostgresRequestExpiryAuthorityTests.Concurrent_sweeps_cannot_expire_durably_accepted_request_from_stale_memory
S08GatewayCloseoutTests.Submit_SeatsOfferingJeeber_OnConversation_AsJeeberOfferer
S08GatewayCloseoutTests.Submit_WhenChatFlagOff_DoesNotSeat_StillReturns201
S08GatewayCloseoutTests.Submit_WhenChatSeatingThrows_StillReturns201_DegradeDontFail
S08GatewayCloseoutTests.Submit_WhenNoConversationIdOnRequest_DoesNotSeat_StillReturns201
Tiers.AdminTierTtlGuardTests.T3_2_Acknowledged_Ttl_Change_Applies_And_Shortens_The_Live_Deadline
```

**#373 added zero failures and fixed zero failures.** It is CI-neutral.

The PR's own 10 new tests
(`GatewayDirectPushDispatchGuardHandlerTests`) all **passed** in the same run.
Compilation was clean — the build step reported `0 Error(s)`; the `build` job
failed on tests, not on compile.

## Root cause of the standing red

Two independent, pre-existing causes:

1. **`build` / `build-and-test`** — the integration suite needs backing
   infrastructure the CI runner does not provide. The logs show repeated
   `System.Net.Sockets.Socket` connect failures and
   `JeebNotificationCatalog seeding attempt 1/6 failed; retrying in 2s`.
   The `Postgres*` and chat-provisioner tests are the ones that fail, which is
   consistent with no Postgres and no chat-service reachable from the runner.

2. **`stateless-gate`** — the R9 gate fails deliberately, on architectural debt:

   ```
   FAIL: gateway DB seam(s) not on the GR-3 allowlist
     src/JeebGateway/Financials/PostgresSettlementLedgerClient.cs
   R9 gate FAILED — gateway is not stateless. See ADR-001-rev2.
   ```

   Plus five `DEBT:` warnings for durable-domain `InMemory*` registrations
   (`IRefreshTokenStore`, `IDisputeCaseStore`, `IDisputeStore`,
   `IJeeberRestrictionStore`, `IAdminEscalationStore`).

   This is the same finding the architecture ownership review records as
   `gateway_has_database: true` against a target of `false`. See
   `jeeb-infrastructure/docs/architecture-review/`.

## Scope of the waiver

This waiver covers **PR #373 only**. It is not a general licence to merge on
red. Any future merge onto a red `main` needs its own waiver, and must
demonstrate the same thing #373 demonstrates: an unchanged failing-test set
plus green tests for the change itself.

## What still has to happen

The waiver ratifies the merge. It does **not** retire the underlying problems:

- `main` remains red. Restoring a trustworthy CI gate needs the integration
  suite to either provision its dependencies or be split from the unit tests.
- The R9 stateless gate stays failing until `PostgresSettlementLedgerClient`
  moves to its owning service and the `InMemory*` durable stores are rewired.
- **#373 is merged but its deploy gate is still live.** The committed default
  disables gateway direct push sends. Deploying it before notification-service
  is verified as the sole push producer would leave push with **no** producer.
  Order: notification-service durable dispatcher verified first, gateway after.
  Emergency rollback without a redeploy:
  `PushNotificationServiceApi__GatewayDirectDispatch__Enabled=true`.
