# 0003 — S02 Wave-1: dual-role identity model with split-signer token authority

**Date:** 2026-06-06
**Status:** Accepted (resolves DECISION #1 of [ADR-0002](0002-s02-identity-role-refresh.md))
**Deciders:** Owner (Ouday) — binding OWNER DECISIONS; Senior Domain Architect (cto-S02); Senior Software Architect
**Technical story:** JEB-37, JEB-38, JEB-39, JEB-1422 (S02 Wave 1)

---

## Context and problem statement

ADR-0002 designed S02 but left DECISION #1 (the canonical role taxonomy on shared `user-management`) owner-gated and Wave 1 blocked. The owner has now ruled, and the ruling also **corrects a latent error in ADR-0002's N11 invariant**: ADR-0002 recorded "the gateway is the ONLY signer of the session JWT; UM never signs" as universal, but the S02 e2e spec (CP-C / STEP H-B4) and the owner require the **role-switch path** to re-issue the JWT in `user-management`, with the gateway signing nothing on that path. The two paths have two different signers. This ADR locks the dual-role model and that split.

**Bounded context:** Identity & Access (shared) vs. the Jeeb product BFF (thin). `user-management` (live `:10001`, 6+ products) owns the Identity aggregate. `jeeb-gateway` owns no identity state; it is an anti-corruption / translation layer between the live identity vocabulary and the frozen Jeeb contract.

**Drivers:**
- Every Wave-1 body assertion is frozen on `{client, jeeber}` snake_case (`available_roles`, `active_role`).
- `user-management` is SHARED — it must stay PRODUCT-AGNOSTIC: no Jeeb `{client,jeeber}` vocabulary may leak into it. Roles are opaque strings.
- Token-authority must be unambiguous per path so no two signers race on the same token.
- Additive-only, sibling-safe, migrate-then-start, owner-gated on the UM change.

---

## Decision drivers (ranked)

1. **Product-agnostic shared UM** — zero Jeeb vocabulary in a 6-product service.
2. **Frozen contract** — Wave-1 bodies are `{client,jeeber}`/snake_case, immutable.
3. **Unambiguous token authority per path** — exactly one signer per path; no drift.
4. **Reuse over build** — UM already owns identity; gateway already owns the OTP-login mint.
5. **Strict testability** — each rule turns a specific red `:3040` assertion green E2E.

---

## Decision outcome

### DECISION #1 — Role taxonomy: gateway translates, UM stays opaque (BINDING)

The **gateway** is the anti-corruption layer that TRANSLATES the live identity roles (`customer` / `driver`) to the frozen Jeeb contract `{client, jeeber}` and emits snake_case `available_roles` / `active_role`. **`user-management` stores OPAQUE role strings and contains ZERO Jeeb `{client,jeeber}` vocabulary.** The `customer ⇄ client`, `driver ⇄ jeeber` mapping lives only at the BFF boundary. Backfill of legacy UM rows uses the existing opaque default role string (e.g. `available_roles=['<opaque-default>']`), never `client`/`jeeber`.

This makes the gateway translation **net-new** (no sibling owns a Jeeb-role decoration; atlas confirms UM is a generic identity store) and correctly places it in the product BFF, not the shared service.

### DECISION #2 — Split-signer token authority (BINDING; corrects ADR-0002 N11)

Two signing paths, two signers — locked:

| Path | Trigger | Signer | Rationale |
|---|---|---|---|
| **OTP-login mint** | `POST /v1/auth/otp/verify` (find-or-create + first session) | **gateway** | the gateway owns the Jeeb session-establishment ceremony and the role decoration; UM has no Jeeb role context at sign-in |
| **Role-switch re-issue** | `POST /v1/users/me/role/switch` | **user-management** | UM is the token AUTHORITY on a role change — it persists `active_role` and re-issues the access+refresh pair atomically with the state change, so the token can never disagree with the persisted role. **Gateway MUST NOT sign on this path** — it forwards the request and relays UM's re-issued pair verbatim (N11, CP-C). |

`/auth/tokens` no-credential mint backdoor is left **AS-IS** per owner; the OTP gate must still not fall back to it (N7.x).

**Chosen option: A** (gateway-decorated dual-role over additive opaque-string UM columns + UM-authoritative re-issue on switch). **Confidence: High** — taxonomy and signer split are now owner-fixed; the seams already exist.

---

## Aggregates, invariants, events

**Identity aggregate (owned by `user-management`):**
- Root: `User`. New additive fields: `available_roles` (text[]/jsonb, nullable), `active_role` (text, nullable), `phone_hash` (text, nullable, UNIQUE via deterministic `HMAC-SHA256(phone,pepper)` index — no raw E.164 column).
- **Invariant I1 (CHECK `CK_Users_ActiveRole_InAvailable`):** `active_role IS NULL OR active_role = ANY(available_roles)`. Legacy NULL rows pass.
- **Invariant I2 (idempotent identity):** one row per phone. `find-or-create` is keyed on `phone_hash`; a returning phone returns the same `userId`, `isNew:false`, NO duplicate row.
- **Invariant I3 (role-switch authority):** persisting `active_role` and re-issuing the JWT pair happen in UM as one authority step; the switch is rejected (`role_not_available`) unless `role ∈ available_roles`.

**Gateway translation layer (owns NO state):**
- Decorates identity DTOs with the Jeeb snake_case contract; applies the gateway-local whitelist `{client,jeeber}`.
- **Invariant I4 (bearer-only subject):** `userId` is always resolved from the bearer principal, never the request body (anti-escalation).

**Domain events:**
- `role.switched { userId, from, to, correlationId }` — emitted by the gateway after a successful UM re-issue (S08 visibility, S12 FCM topic-group `jeeb_jeebers`).

**Ubiquitous language (frozen):** contract vocabulary is `client`, `jeeber`, `available_roles`, `active_role`, `isNew`, `phoneHashRef` (all snake_case in bodies). UM-internal vocabulary is opaque (`customer`/`driver`/`<opaque-default>`). The translation table is the ONLY place the two meet.

---

## Contract surface & error taxonomy (frozen by S02 asserts)

| Route | Owner of logic | Signer | Key assertions |
|---|---|---|---|
| `POST /v1/auth/otp/verify` (decorated) | gateway orchestrates UM find-or-create + mints | gateway | CP-A `[client]`, CP-B `[client,jeeber]`, access+refresh present |
| `GET /v1/users/me` (NEW) | gateway translates UM profile; fixes live UM profile 500; 30s cache | n/a (read) | H-A3/H-B3/H-B5; userId from bearer (I4) |
| `POST /v1/users/me/role/switch` (NEW) | gateway whitelist+forward; **UM** persists+re-issues | **UM** | CP-C `active_role=jeeber`, UM-issued token decodes role=jeeber, gateway signed nothing (N11) |

Error taxonomy (distinct, RFC7807): `invalid_role` **400** (unknown value, NO UM call — N6) ≠ `role_not_available` **403** (valid value not in `available_roles` — UM distinct signal G4, N5/ALT-1). `invalid_country` 400 (N3), `invalid_phone` 400 (N4), `rate_limited` 429 (N12) are gateway-local (Wave 0). Refresh reuse → 401 + whole family revoked (N8).

---

## Anti-corruption boundary (the seam)

```
live identity (customer/driver, opaque) ──translate──▶ Jeeb contract (client/jeeber, snake_case)
        ▲ user-management owns this                          ▲ jeeb-gateway owns this
        │ persists opaque active_role                        │ NEVER persists; pure mapping
        │ re-issues JWT on switch (authority)                │ relays UM token; mints only OTP-login
```

If the live role vocabulary ever changes, only the gateway translation table changes — UM and the 6 siblings are untouched. This is the anti-corruption guarantee.

---

## Consequences

### Positive
- Shared UM stays product-agnostic; zero Jeeb vocabulary; 6 siblings unaffected (additive-only).
- Token can never disagree with persisted role on switch (UM is the single authority on that path).
- Gateway stays thin: translation + orchestration only, no identity state.
- Idempotent phone identity; no raw E.164 stored or logged (phone_hash only).

### Negative / trade-offs
- Two signers exist (one per path) — operationally each must hold a valid signing config; mis-wiring (gateway accidentally signing the switch) is a CP-C regression. Mitigated by the N11 assertion on the real console.
- UM column add requires owner approval + coordinated migrate-then-start (never auto-migrate on boot).

### Risks and mitigations
| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Gateway accidentally signs on switch path | Med | High | N11/CP-C strict assertion: decoded token must be UM-issued; gateway code path has no signer on switch |
| Jeeb vocabulary leaks into UM diff | Med | High | N14 boundary-scan: zero net-new Jeeb identifiers in UM PR; opaque-string columns only |
| UM migration not applied on deploy | Med | High | idempotent SQL, migrate-then-start; green CP-A/CP-B = proof migration ran (N10 audit-only) |
| Dual-role seed faked | Low | High | seed Kamal with BOTH opaque roles for real; no mock — green CP-B is the proof |

---

## Links
- Resolves DECISION #1 of [ADR-0002](0002-s02-identity-role-refresh.md); corrects its N11 to the split-signer model (DECISION #2).
- Related: JEB-37/38/39/1422; gaps G4 (distinct role-not-available), N10 (schema-drift audit-only), N11 (split-signer authority), N14 (UM vocabulary boundary scan).
- Reuses: UM Identity aggregate (extend), gateway OTP-login mint seam + refresh rotation seam (existing).
