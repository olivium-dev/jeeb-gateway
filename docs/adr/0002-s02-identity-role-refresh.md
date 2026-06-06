# 0002 — S02 identity: dual-role profile, role-switch, refresh rotation, phone policy

**Date:** 2026-06-06
**Status:** Accepted (Wave 0). Wave 1 / DECISION #1 resolved and N11 split-signer corrected by [ADR-0003](0003-s02-wave1-dual-role-identity-split-signer.md).
**Deciders:** Senior Software Architect (cto-S02); DECISION #1 ruled by owner in ADR-0003
**Technical story:** JEB-37, JEB-38, JEB-39, JEB-1422, JEB-1430 (S02)

---

## Context and problem statement

S02 needs five buildable features wired through the REAL `:3040` console path (strict body assertions, no mocks): gateway phone policy + rate-limit (F-E), `/v1/auth/refresh` family rotation (F-D), dual-role verify DTO (F-C), `GET /v1/users/me` (F-B), and `POST /v1/users/me/role/switch` (F-A). Two standing invariants bound the design: the gateway is a THIN orchestrator (no identity/role business state), and SHARED `user-management` may only change additively. A ground-truth scan established what already exists vs. what is genuinely unbuilt.

**Ground-truth findings (verified, not assumed):**
- F-D rotation logic **already exists** in the gateway: `ITokenService.RefreshAsync` returns `RefreshOutcome.ReuseDetected`; `IRefreshTokenStore.RotateAsync`/`RevokeChainAsync` implement rotate-on-use + full-chain revoke; tokens stored as SHA-256 hash. The ONLY gap is the store is in-memory (`IRefreshTokenStore` doc: "MVP implementation is in-memory; production wiring will move to Postgres"). → F-D is **wrap/extend (durable store)**, not net-new.
- UM `User` entity (`Data/Entities/User.cs`) has **no role columns** (`Id, Email, Password, Username, ReferralCode, CreatedDate, DateOfBirth, ResetToken*, SocialId, SocialPlatform, ProfilePic`). → role taxonomy is genuinely unbuilt; F-A/F-B/F-C need additive UM columns.
- UM exposes `GET /api/User/profile/{userId}` (path-param), no `available_roles`/`active_role`. Gateway has `POST /v1/auth/otp/verify` (AuthOtpController) that mints the session via `IUsersStore.GetOrCreateAsync` + `ITokenService.IssueAsync` — the correct token-authority seam.
- `GET /v1/users/me` and `POST /v1/users/me/role/switch` do not exist (404).

**Drivers:**
- Every Wave-1 body assertion is frozen on `{client, jeeber}` snake_case (`available_roles`, `active_role`). Building Wave 1 before DECISION #1 forces a unilateral redefinition of the identity model — forbidden.
- Token-authority invariant (N11): the gateway is the ONLY signer of the session JWT; UM never signs.
- Thin-gateway invariant: role validity (`available_roles`) is decided by UM (owner of the data); the gateway only does a cheap whitelist guard + orchestration.

---

## Decision drivers (ranked)

1. **Additive-only on shared UM** — no breaking column/route change; 6 sibling products consume UM.
2. **Token-authority invariant** — gateway signs the session JWT; UM persists role + signals re-issue, gateway re-mints.
3. **Reuse over build** — F-D rotation exists; only the store is swapped. F-E is gateway-local, no upstream change.
4. **Frozen contract** — Wave-1 bodies must match `{client,jeeber}`/snake_case exactly; do not ship until DECISION #1 lands.
5. **Strict-assertion testability** — each feature must turn a specific red `:3040` assertion green end-to-end.

---

## Considered options

| Option | Summary |
|---|---|
| **A — gateway-decorated dual-role over additive UM columns** (chosen) | UM adds `available_roles`/`active_role` columns + a generic `RoleSwitch` route with **opaque-string** roles (zero Jeeb vocab); gateway decorates DTOs with Jeeb snake_case + whitelist + JWT mint |
| **B — role logic in gateway, UM untouched** | Gateway stores role/available_roles in its own store and shapes JWT | Rejected: business state in the thin gateway (guardrail/14), and role data has no owning service |
| **C — fork a new jeeb-identity service** | New service owns roles | Rejected: reuse-first; UM already owns identity; net-new = ~100× cost, no boundary justification |

---

## Decision outcome

**Chosen option: A.** Wave-1 identity/role logic lives in UM (the owning service) as additive, opaque-string columns + a generic role-switch route; the gateway is a thin decorator that maps to Jeeb's frozen `{client,jeeber}`/snake_case contract, applies a gateway-local whitelist, and is the sole JWT signer. F-D reuses the existing gateway rotation seam and only swaps the store to a durable backend (M3). F-E is gateway-local rate-limit + phone validation with **no** upstream change.

**Confidence:** High for Wave 0 (F-E, F-D — gateway-local, logic exists). Medium for Wave 1 — correct seam, but the contract is frozen on DECISION #1 (role taxonomy), which is an owner-gated PRODUCT decision and is escalated, not decided here.

**Sequencing:** Wave 0 (F-E, F-D) starts now in parallel, no owner gate. Wave 1 (F-C → F-B → F-A) is BLOCKED until DECISION #1; build order is dependency-ordered (verify DTO → profile read → keystone switch).

---

## Contract surface (OpenAPI sketch — additive, snake_case bodies frozen by S02 asserts)

```yaml
# Gateway BFF — additive routes. Errors are RFC7807 application/problem+json.
paths:
  /v1/auth/otp/verify:        # F-C decoration on EXISTING route (additive fields only)
    post:
      responses:
        "200":
          content: { application/json: { schema: { $ref: '#/c/VerifyResult' } } }
          # NEW additive fields: available_roles[], active_role. access JWT carries role/active_role claims.
  /v1/users/me:               # F-B — NEW (currently 404)
    get:
      security: [ { bearerAuth: [] } ]   # userId from bearer principal, NEVER body
      responses:
        "200": { content: { application/json: { schema: { $ref: '#/c/MeProfile' } } } }
        "401": { $ref: '#/r/Unauthorized' }
  /v1/users/me/role/switch:   # F-A — NEW keystone (currently 404)
    post:
      security: [ { bearerAuth: [] } ]
      requestBody: { content: { application/json: { schema: { $ref: '#/c/RoleSwitchReq' } } } }
      responses:
        "200": { content: { application/json: { schema: { $ref: '#/c/RoleSwitchResult' } } } }  # re-issued JWT pair
        "400": { $ref: '#/r/InvalidRole' }        # type=invalid_role — unknown value, NO UM call
        "403": { $ref: '#/r/RoleNotAvailable' }   # type=role_not_available — distinct UM signal (gap G4)
  /v1/auth/refresh:           # F-D — EXISTS; swap store to durable, behaviour unchanged
    post:
      responses:
        "200": { content: { application/json: { schema: { $ref: '#/c/TokenPair' } } } }
        "401": { $ref: '#/r/ReuseOrInvalid' }     # reuse of revoked token → whole family revoked → re-OTP
components:
  schemas:
    MeProfile:        { required: [user_id, available_roles, active_role] }   # snake_case
    RoleSwitchReq:    { required: [role] }                                    # role ∈ {client,jeeber}
    RoleSwitchResult: { required: [active_role, access_token, refresh_token] }
    VerifyResult:     { required: [access_token, refresh_token, available_roles, active_role] }
# F-E error types (gateway-local, 4xx/429): invalid_country(400) invalid_phone(400) rate_limited(429)
```

---

## Component decomposition — who owns what

| Feature | Gateway (thin orchestration) | user-management (owning service) | Store |
|---|---|---|---|
| **F-E** phone policy + rate-limit | libphonenumber-csharp (region=LB) → 400 `invalid_country`; E.164 parse-fail → 400 `invalid_phone`; sliding window >10/min/IP AND >3/min/phone → 429 `rate_limited`; **downstream SendAsync NOT called when throttled** | none (no upstream change) | counters: durable (M3) or memory; gateway-local |
| **F-D** refresh rotation | EXISTING `ITokenService.RefreshAsync` (rotate-on-use, reuse→chain-revoke→401→re-OTP). **Only change:** bind `IRefreshTokenStore` to durable impl | none | durable (M3, gate only on store-tech) |
| **F-C** verify DTO | decorate verify 200 with snake_case `available_roles`/`active_role`; put `role`/`active_role` claims in access JWT (gateway signs) | additive columns (JEB-38) + find-or-create returns `{userId,isNew,phoneHashRef}` (JEB-1422) | UM Postgres (additive) |
| **F-B** GET /v1/users/me | NEW route; userId from bearer principal; fix real UM `profile` 500-with-valid-bearer; surface `available_roles`/`active_role`; 30s cache (NFR-1) | return roles on profile | UM Postgres |
| **F-A** POST role/switch (KEYSTONE) | validate `role ∈ {client,jeeber}` (gateway-local whitelist) → else 400 `invalid_role` NO UM call; resolve userId from bearer; `RoleSwitchAsync(userId,role)`; relay UM-reissued JWT — **gateway SIGNS NOTHING** of UM's re-issue path beyond its own session mint authority (N11); emit `role.switched` | verify `role ∈ available_roles` (else distinct G4 signal → gateway 403 `role_not_available`); persist `active_role` (CHECK `CK_Users_ActiveRole_InAvailable`); **re-issue JWT pair** | UM Postgres |

**UM additive-only constraints (owner-approval-gated, generic branch):** new nullable columns `available_roles` (text[]/json) + `active_role` (text), **opaque strings** — UM stores no Jeeb vocabulary; a CHECK constraint `CK_Users_ActiveRole_InAvailable` enforces `active_role ∈ available_roles`; new route is generic `RoleSwitch`, EF migration idempotent + applied migrate-then-start (per fleet rule). The `{client,jeeber}` mapping is gateway-only.

---

## Failure modes and how the design handles them

| Failure | Handling |
|---|---|
| Refresh-token replay (theft) | reuse of revoked token → `ReuseDetected` → `RevokeChainAsync` revokes the whole family → 401 → forces re-OTP. Durable store survives gateway restart (M3) so detection holds across deploys. |
| Throttle bypass | F-E checks limit BEFORE `SendAsync`; assertion-provable that the OTP service is not called when 429. Per-IP AND per-phone windows. |
| Role escalation via body | userId resolved from bearer principal ONLY, never request body (F-A/F-B). UM re-checks `role ∈ available_roles` server-side; CHECK constraint is the last line of defense. |
| Unknown role value | gateway whitelist short-circuits → 400 `invalid_role` with **no UM call** (cheap, no upstream load). |
| Role not granted | UM returns a **distinct** signal (G4) the gateway maps to 403 `role_not_available` — separable from 400, asserted distinctly (CP-C). |
| Gateway minting drift | gateway is sole session-JWT signer (N11); UM persists + signals re-issue, gateway re-mints. No second signer. |
| UM migration not applied | a green H-A2 (roles in verify body) proves the migration ran — N10 schema-drift guard is audit-only. |

---

## Test strategy (strict `:3040` body assertions, real path)

- **F-E:** N3/N4/N12 — invalid_country, invalid_phone, rate_limited; assert downstream-not-called via 202-absence + type body match.
- **F-D:** H-A4/N8 — rotate returns new pair; replay of revoked → 401 + re-OTP forced (family revoked).
- **F-C:** H-A2/H-B2 (CP-A new Sami `[client]`, CP-B seeded Kamal `[client,jeeber]`) — jsonEquals on `available_roles`/`active_role` + JWT claim.
- **F-B:** H-A3/H-B3/H-B5/ALT-5.1 — `me` returns dual-role profile; userId from bearer.
- **F-A:** H-B4 (CP-C), ALT-1/3/5.2, N5/N6/N11 — switch persists, re-issues JWT, 400 vs 403 distinct, gateway signs nothing extra.
- **No build:** N2/N2b attempt-cap already real-green in one-time-password (count-based, 3-wrong lockout); ALT-5 jeeber-grant is an S03 seam (assert in combined suite); N10 audit-only.

---

## Consequences

### Positive
- Reuses the existing gateway rotation seam (F-D) — only a store binding changes; logic untouched and already proven.
- UM stays generic (opaque-string roles) — zero Jeeb vocabulary leaks into a shared service; the 6 siblings are unaffected.
- Token-authority invariant preserved; identity logic in its owning service; gateway stays thin.

### Negative / trade-offs
- Wave 1 is hard-blocked on a human PRODUCT decision (DECISION #1) — no code can ship for F-A/F-B/F-C until taxonomy is fixed. Accepted: building first = forbidden unilateral identity redefinition.
- Durable refresh store (M3) adds a dependency on the gateway hot path; gate only the store-tech choice, not the rotation logic.
- UM column add requires owner approval + coordinated migrate-then-start.

### Risks and mitigations
| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| DECISION #1 stalls Wave 1 | Med | High | Wave 0 ships value independently; escalate the precise decision now |
| UM migration not applied on deploy | Med | High | idempotent EF migration, migrate-then-start; H-A2 green = proof |
| Durable store outage breaks refresh | Low | Med | gate M3 tech choice; reuse-detection correctness > availability for security |

---

## ESCALATION — DECISION #1 (owner-gated, blocks Wave 1)

**Decision required:** the canonical role taxonomy persisted in shared UM and surfaced in the frozen S02 contract.
**Precise question:** Are the two roles `{client, jeeber}` (snake_case, as every Wave-1 body assertion is frozen), OR does the live system's "customer" role need reconciling — i.e. is `client` == the live `customer` role, or a new value? This determines the exact strings persisted in UM `available_roles`/`active_role` and emitted by the gateway.
**Why owner-gated:** redefining the identity model on a shared service consumed by 6 products is a product decision, not an architectural one; building before it is answered forces a unilateral redefinition (forbidden). Wave 0 is unaffected and proceeds now.

---

## Links
- Related: JEB-37, JEB-38, JEB-39, JEB-1422, JEB-1430; gaps M3 (durable store), G4 (distinct role-not-available signal), N10 (schema-drift guard), N11 (token authority)
- Reuses: `ITokenService`/`IRefreshTokenStore` rotation seam (gateway, already implemented)
