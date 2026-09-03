# 0013 — Route durable push delivery through notification-service

**Date:** 2026-08-29
**Status:** Accepted (retroactive clarification of the 2026-08-15 through 2026-08-19 owner decisions)
**Deciders:** Jeeb owner decisions merged in gateway PRs #441, #474, and #512
**Technical story:** [PR #441](https://github.com/olivium-dev/jeeb-gateway/pull/441), [PR #474](https://github.com/olivium-dev/jeeb-gateway/pull/474), [PR #512](https://github.com/olivium-dev/jeeb-gateway/pull/512)

---

## Context and problem statement

The July push target/compliance documents state that microservices must not call
the generic push relay directly and that delivery returns through the gateway.
Later owner-authored, merged decisions changed the notification-delivery owner:
PR #441 made notification-service own the offer category in every rung, PR #474
moved the remaining gateway push consumers to notification-service and deleted
the in-gateway push stack, and PR #512 permanently blocked gateway direct push
and named notification-service the settled producer.

This ADR records the current narrow exception without rewriting the historical
documents: domain producers hand durable notification commands to
notification-service; only notification-service may call the generic
push-notification relay. Other domain services may not bypass that owner.

**Drivers:**
- A notification must be durably accepted before device delivery is attempted.
- Exactly one producer may dispatch each notification to FCM.
- The shared push relay must remain product-neutral.
- Internal credentials must grant only the notification-to-relay capability.
- Rollout and rollback must preserve compatibility while versions overlap.

## Decision drivers (ranked)

1. Single-producer correctness and durable idempotency.
2. Fail-closed readiness before any live mutation.
3. Least-privilege authentication at the owner-to-relay boundary.
4. Zero-downtime, independently deployable services.

## Considered options

| Option | Summary |
|---|---|
| **A — notification-service is the settled producer** | Domain/gateway handover is durable; notification-service alone calls the generic relay. |
| **B — gateway dispatches push** | Restore the deleted gateway push stack and route notification delivery back through it. |
| **C — every domain service calls the relay** | Each producer manages relay auth, retries, idempotency, and delivery ownership. |

## Decision outcome

**Chosen option:** **A — notification-service is the settled producer**, because
it matches the later merged owner decisions and keeps durable ownership,
idempotency, and retries in one place. The gateway remains a producer/BFF and
must not dispatch push directly.

**Confidence level:** High — this is a record of three later owner-authored,
merged decisions and the direct-send guard already enforced in the gateway.

The allowed call graph is:

```text
gateway or domain producer -> durable notification-service handover
                           -> generic push-notification relay -> FCM
```

The July direct-callback rule is superseded only for the designated
notification-service → push-notification owner path. It remains binding for all
other domain microservices.

### Canonical Firebase and activation contract

The non-secret machine contract is
[`contracts/jeeb-firebase-v1.json`](../../contracts/jeeb-firebase-v1.json). Version
1 fixes the Jeeb project to `jeeb-5a293` / project number `1051234312170`, uses
the Firestore `(default)` database, declares chat capability enabled, and names
`notification-service` as the only durable push producer.

The gateway binds the same values through strongly typed startup validation.
Development/test configuration keeps `FeatureFlags:UseUpstream:Chat=false`;
only a separately reviewed staging B target and the coordinated production
target set it to `true`. Deploy workflows remove case-skewed and historical
Firestore database aliases before adding the exact contract rows, so Docker
Swarm cannot retain a stale false value or named database selector.

`FeatureFlags:PushDispatchMode` is permanently pinned to `local`, whose current
implementation is the durable notification-service hand-over. The historical
`upstream-authority` rung would make the gateway call push-notification directly
and is rejected at host startup. The unconditional HTTP guard remains a second
independent boundary and still permits only token registration/deletion,
health, and idempotency recovery.

### Authentication boundary

- The relay derives identity and scope only from the credential matched by
  `X-Api-Key`; `X-Caller-Id` can confirm that identity but can never select or
  elevate it. A missing, forged, or swapped identity header fails. The two supported
  identities use different credentials: `notification-service` receives only
  `notification.user-delivery`; `jeeb-gateway` receives only
  `gateway.registration` and `gateway.recovery`.
- notification-service reads its value from an absolute, regular, bounded,
  UTF-8 secret file mounted mode `0400` for UID/GID `10001:10001`.
- The gateway retains a distinct file-backed credential for the surviving
  device-registration, deletion, and approved idempotency-recovery BFF surface.
  Its permanent outbound guard rejects every device, user, broadcast, and topic
  send route before the credential can authorize a direct dispatch.
- The provider verifies cross-denial: a valid notification credential is still
  forbidden from gateway operations, and a valid gateway credential is still
  forbidden from user delivery.
- The key grants no database, Firebase, notification-admin, or DLQ privilege.
- Secret material never appears in service specs, command arguments, logs, or
  persisted outbox/DLQ documents. Rotation uses immutable content-addressed
  Swarm secrets and overlapping compatible tasks.
- Live migration is provider-first expand/contract. `expand` requires three
  distinct immutable credentials. The retained old key is a synthetic
  `legacy-shared` principal, accepted only on the exact pre-existing operation
  allowlist, only without `X-Caller-Id`, and never on new scoped readiness.
  `strict` rejects legacy-key configuration and traffic entirely. This mode is
  independent from `PUSH_PIPELINE_REQUIRED`; false is never an expand mode.

## Consequences

### Positive
- One durable owner controls retry, replay, and end-to-end idempotency.
- The relay stays generic and independently verifiable through the shared
  executable contract.
- Gateway restarts or feature-flag drift cannot reintroduce a second producer;
  canonical identity or push-rung drift fails startup and deployment gates.

### Negative / trade-offs
- notification-service startup/readiness depends on MongoDB, its worker, and
  valid relay configuration/credential resolution. Transient downstream relay
  availability is checked by a non-delivering deploy probe, not by steady-state
  readiness, so durable acceptance remains available during a relay outage.
- A relay contract change requires coordinated consumer/provider verification.
- Reverse rollback order differs from forward deployment order.

### Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Credential mismatch rejects dispatch | Medium | High | Distinct content-addressed secrets, non-delivering deploy probe, provider contract. |
| Duplicate send during retry | Low | High | One notification ID in header and body; provider same-key replay. |
| Failed replacement removes incumbent | Low | High | Swarm ingress/VIP, image healthcheck, start-first update, pause on failure. |
| Historical document is treated as current | Medium | Medium | This ADR cites and orders the superseding decisions explicitly. |

## Pros and cons of the options

### A — notification-service is the settled producer

**Pros:** durable single owner; generic relay; matches current code and owner decisions.

**Cons:** adds an authenticated owner-to-relay dependency and coordinated contract gate.

### B — gateway dispatches push

**Pros:** follows the older topology diagram.

**Cons:** restores deleted code, creates two producers, and contradicts #441/#474/#512.

### C — every domain service calls the relay

**Pros:** fewer handover hops.

**Cons:** duplicates retry/auth/idempotency logic and bypasses the durable owner.

## Review, activation, and rollback order

- Merge/review order: notification-service PR #61 → gateway PR #548 →
  push-notification PR #31, with a deployment hold on both callers. Merge order
  grants no authority to activate a caller and no PR image may be deployed.
- Live activation order: protected-main push-notification in `expand` →
  notification-service → gateway → the same reviewed push-notification digest
  in `strict` → ephemeral.
- Before strict, move only a failing caller to a recorded prior digest while the
  provider remains expand-compatible. After strict, first apply a new
  start-first provider update back to expand, then select caller digests. Return
  to the original shared-only provider only after every caller is confirmed on
  the legacy credential.
- Exact readiness and rollback gates are in
  [the push delivery rollout runbook](../runbooks/push-delivery-rollout.md).

## Links

- Supersedes: July 2026 push target/compliance direct-callback rule, only for the notification-delivery owner path.
- Superseded by: N/A.
- Related: gateway PRs #441, #474, #512; notification-service PR #61; push-notification PR #31; ephemeral PR #24.
