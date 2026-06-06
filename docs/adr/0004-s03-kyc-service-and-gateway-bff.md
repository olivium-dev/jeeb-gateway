# 0004 — Extract a kyc-service that owns the KYC domain + store; gateway stays a thin KYC BFF

**Date:** 2026-06-06
**Status:** Accepted
**Deciders:** Senior Software Architect (software-arch-1), with Staff Tech Lead direction + Senior Domain Architect boundary
**Technical story:** S03 KYC submission & ToS signing — closes the S03 reds (DEC1/DEC2/DEC3 + H8 role grant); unblocks S02 ALT-5.2

---

## Context and problem statement

S03 (`kyc-submission-tos-signing`) requires: a JSON-refs KYC submit (`POST /kyc/submit`) → `201 Submitted` with
idempotency, an admin review (`PATCH /admin/kyc/{id}/review`) that **grants the jeeber role**, a ToS-sign
(`POST /v1/kyc/contract-template/sign`) that stamps `tos_signed_at`, and a CDN signed-PUT upload broker
(`POST /api/cdn/assets`).

Today the KYC domain lives **inside `jeeb-gateway`**: `IKycStore` (in-memory), `IKycService` (state machine
`Draft→Submitted→Verified|Rejected`), and — the worst offender — `KycService.UnlockJeeberRoleAsync` which writes
the role grant to an **in-memory `IUsersStore` in the gateway**. The MVP submit is **multipart** (raw bytes through
the gateway), not the JSON-refs contract S03 asserts; there is no ToS-sign sub-step and no CDN upload-URL broker.

This violates two fleet laws: (1) **the gateway is a thin BFF** — it must hold no business state, no domain state
machine, no data store; (2) **a microservice owns its own domain + data store** and services are loosely coupled,
composed only in the gateway. The KYC business rules (the SM-6 state machine, the document slot model, the role-grant
decision, the `tos_signed_at` stamp, idempotency dedup) are real business logic and real business state — they belong
in an owning microservice, not the gateway.

**Drivers:**
- The role grant (`Submitted→Approved` appends `jeeber`) is the single most safety-critical transition in Jeeb; it must be admin-gated and durable, not an in-memory gateway store that resets on redeploy.
- KYC submissions + ToS signatures are durable business state with a state machine and idempotency — pure domain.
- form-builder/contract-signing/cdn are already-live generic primitives; per ARCH LAW the kyc-service must NOT call them — only the gateway BFF composes them.
- No mocking: validate via the real strict :3040 console harness (`suite-run.mjs S03`).

---

## Decision drivers (ranked)

1. **Boundary correctness (ARCH LAW):** KYC domain + data store leaves the gateway and lands in an owning kyc-service; kyc-service depends on NO other microservice.
2. **Durable, admin-gated role grant:** the identity mutation must survive redeploy and be the ONLY identity-mutating transition.
3. **Thin gateway:** gateway composes kyc-service + form-builder + contract-signing + cdn via typed clients only — zero KYC logic/data.
4. **Console-harness GREEN with no relaxed assertions:** the rewired S03 routes (`/kyc/submit` JSON, `PATCH /admin/kyc/{id}/review`, `/v1/kyc/contract-template/sign`, `/api/cdn/assets`) must pass live.
5. **Follow the siblings:** olivium-microservice-template, .NET 8, deploy-then-migrate, sibling appsettings pattern.

---

## Considered options

| Option | Summary |
|---|---|
| **A — New kyc-service owns domain+store; gateway is a thin KYC BFF composing kyc-service + form-builder + contract-signing + cdn** | Extract per ARCH LAW. kyc-service holds the SM-6 state machine, submissions, signatures, idempotency, role-grant decision; gateway orchestrates only. |
| **B — Keep KYC in the gateway, just add the JSON routes + a durable DB on the gateway** | Fastest to green; gateway grows a DB + business state. |
| **C — Fold KYC into user-management (it already owns roles)** | Roles live in UM; co-locate KYC there. |

---

## Decision outcome

**Chosen option: A — new `kyc-service` owns the KYC domain + its own data store; the gateway is a thin KYC BFF.**

kyc-service is a sibling-following .NET 8 microservice scaffolded from `olivium-microservice-template`, with its own
PostgreSQL database (`jeeb_kyc`), migrate-then-start, deploy via the per-repo GHA workflow. It owns: the KYC submission
aggregate, the SM-6 state machine, the ToS-acceptance record (`tos_signed_at` + `tos_accepted_version`), idempotency
dedup, and the admin review → role-grant DECISION. It calls **no other microservice**. The gateway KYC BFF
(`KycBffController`, `KycController` rewired, `AdminKycController` rewired, `CdnController` extended) orchestrates:
it composes kyc-service (the Jeeb KYC domain), form-builder (form schema as DATA), contract-signing (the generic
sign ceremony), cdn (signed-PUT broker), and user-management (the actual `available_roles` jsonb append + token
re-issue, triggered by the gateway when kyc-service reports an approve outcome).

**Boundary nuance (load-bearing):** kyc-service decides "this approval grants jeeber"; it does NOT itself mutate
user-management (that would be a service→service call, banned). The gateway BFF performs the cross-service
composition: on `PATCH /admin/kyc/{id}/review {approve}`, the gateway calls kyc-service (review → outcome
`{status:verified, grantsRole:"jeeber"}`), then calls user-management to append the role + re-issue tokens. KYC
state lives in kyc-service; role truth lives in user-management; composition lives in the gateway. (This supersedes
ADR-0003's interim gateway-mint/in-memory-IUsersStore find-or-create for the role surface — see Links.)

**Confidence level:** High — mirrors the established sibling `*-service` + thin-gateway pattern; the existing
KycService state machine is lifted, not reinvented; contract-signing/cdn/form-builder typed clients already exist.

---

## Consequences

### Positive
- Gateway becomes boundary-compliant: no KYC data, no state machine, no in-gateway role store.
- Role grant becomes durable (Postgres `jeeb_kyc` + UM jsonb append), survives redeploy, admin-gated.
- S03 reds close against REAL routes; S02 ALT-5.2 (Sami → jeeber-eligible after approval) unblocks.
- kyc-service is independently deployable/scalable and testable in isolation.

### Negative / trade-offs
- New repo + new DB + new deploy workflow = infra that needs owner action (ESCALATE — see Infra section).
- One extra network hop (gateway→kyc-service) on the KYC path; budgeted with a Polly timeout+breaker (p99 < 800ms).
- The MVP multipart `/kyc/submit` contract changes to JSON-refs; mobile must move to the signed-PUT-then-refs flow (additive: keep multipart alias during transition, deprecate per policy).

### Risks and mitigations
| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| New repo/DB blocks the build (owner-gated) | High | High | ESCALATE concisely now; gateway BFF + contract are designable in parallel against the kyc-service OpenAPI contract |
| Boundary leak: kyc-service calls form-builder/contract-signing/cdn | Med | High | N15 git-diff governance scan; kyc-service has NO typed clients to other services; composition only in gateway |
| Role grant double-applied on idempotent replay | Med | High | Idempotency-Key dedup in kyc-service; UM jsonb append is set-semantics (no dup role) |
| Approval rolled back if notification fails | Low | High | Notification is async, off the critical path (N14); approve commits regardless |

---

## Pros and cons of the options

### A — kyc-service + thin gateway BFF
**Pros:** ARCH-LAW compliant; durable; isolated; mirrors siblings. **Cons:** new infra (repo/DB/deploy) needs owner.

### B — Keep KYC in the gateway + gateway DB
**Pros:** fastest to green. **Cons:** gateway grows business state + a DB → direct violation of thin-BFF law; not acceptable.

### C — Fold KYC into user-management
**Pros:** co-locates with role truth; no new repo. **Cons:** UM is a SHARED service (7 products); injecting Jeeb KYC domain into it is a cross-product boundary leak and a blast-radius-7 change. Rejected.

---

## Links
- Supersedes (role surface only): [ADR-0003](0003-s02-wave1-dual-role-identity-split-signer.md) — gateway in-memory IUsersStore find-or-create is replaced by kyc-service decision + UM jsonb append for the KYC-driven grant.
- Related: e2e-S03 spec `jeeb-scenarios/e2e/e2e-S03-kyc-submission-tos-signing.md`; console `data/scenarios/scenario-S03.json`; gateway `Controllers/KycBffController.cs`, `AdminKycController.cs`, `KycController.cs`, `CdnController.cs`.
