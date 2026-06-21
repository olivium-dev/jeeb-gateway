# 0006 — Migrate the gateway's remaining in-memory stores to jeeb-state-service (audit + the one upstream primitive that unblocks the rest)

**Date:** 2026-06-20
**Status:** Accepted (audit) — remaining migrations BLOCKED on one upstream primitive (see "Decision")
**Deciders:** gw-stores work-stream (run `temp-overall-run-1`)
**Technical story:** Close out ADR-0001 (gateway stateless + thin) / ADR-0005 (state + Support → jeeb-state-service) by moving the *remaining* in-gateway in-memory stores behind jeeb-state-service.

---

## Context and problem statement

ADR-0001 says the gateway holds **no business state** and ADR-0005 routes durable state (and Support)
to **jeeb-state-service**. Most of the migration has already shipped (see "What is already migrated").
This work-stream audited **every** remaining `InMemory*Store` still registered in
`src/JeebGateway/Program.cs` to decide what can move now, what must wait, and exactly why.

The audit's headline finding: **the remaining stores cannot be migrated with the state-service's
current contract.** They all require at least one of two query shapes the state-service does not
expose:

1. **list / prefix-scan by a domain key** (e.g. "all tickets for owner X", "the admin moderation
   queue", "all disputes for user Y", "the next queued data-export row"), and/or
2. **mutable update of an existing row** (state-machine transitions like
   `Scheduled → Completed`, `Pending → Accepted`, `queued → processing → ready`).

The two generic primitives the state-service exposes today are:

- **R1 — idempotency KV**: `PUT /idempotency` (insert-once, `ON CONFLICT (key) DO NOTHING`) +
  `GET /idempotency/{key}` (GET-by-key). This is the *only* general key→opaque-body store, and it
  is **insert-once + GET-by-primary-key only** — no list, no prefix scan, no in-place update.
- **R8 — locks / rate-limit**: keyed by `lockKey` / `bucket`.

Plus a set of **purpose-built relational endpoints** (refresh families, KYC, ratings, disputes,
strikes, OTP-escalation, cancellation counters) that are write-only / id-keyed (no GET-by-domain-key).

The codebase already documents this gap honestly in
`StateServiceSupportTicketStore.ListByOwnerAsync` (returns empty, "BLOCKED-ESCALATE: no upstream
list-by-owner / prefix-scan primitive") and in `StateServiceWriteThroughServices.cs` ("CONTRACT
GAP … the gateway cannot REBUILD its in-memory index from the state-service after a bounce").
This ADR generalises that finding across the whole remaining store surface and names the fix.

---

## What is already migrated (no action — verified in this audit)

Wired under the `stateServiceWired` flag in `Program.cs` (≈ lines 1730–1788), degrading to the legacy
in-memory store only on a local/CI run with no live state-service:

| Domain | Store / writer | State-service surface | Bounce-survivable? |
| --- | --- | --- | --- |
| R1 idempotency | `StateServiceIdempotencyStore` | `PUT/GET /idempotency` | **Yes** (GET-by-key) |
| Support tickets | `StateServiceSupportTicketStore` (create + get-by-id) | R1 KV `support-ticket:{id}` | **Create+get yes**; list-by-owner blocked (see below) |
| Offer→request routing | `StateServiceOfferRequestIndex` | R1 KV | **Yes** (GET-by-key) |
| R8 rate-limit / locks | `StateServiceRateLimitStore` / `StateServiceLockStore` | `HitRateLimit` / `Acquire/ReleaseLock` | **Yes** (keyed) |
| Request-expiry sweep | `StateServiceRequestExpiryRecorder` | R1 KV | Writes durable; degrade-to-noop |
| R2 refresh families | `StateServiceRefreshFamilyWriter` | `CreateRefreshFamily` / `RotateRefreshToken` / `IsRefreshFamilyRevoked` | **Write-through** (revocation durable); read-index stays in-memory |
| R3 KYC | `StateServiceKycWriter` | `CreateKyc` / `GetKyc` / `UpdateKyc` | Write-through; by-subject read is the gap |
| R4 ratings | `StateServiceRatingWriter` | `SubmitRating` / `RevealRatings` | Write-through |
| R5 disputes | `StateServiceDisputeWriter` | `OpenDispute` / `TransitionDispute` (version-checked) | Write-through; concurrent-resolve is race-safe server-side |
| R6/R7 strikes / OTP-escalation | `StateServiceStrikeWriter` | `AddStrike` / `BumpCancellationCounter` / `EscalateOtp` | Write-through |

For R2–R7 the **write** is durable (the security-critical bits — family revocation, dispute
version-conflict — are enforced server-side and survive a bounce); the gateway keeps its in-memory
store only as the **fast read/query index**, because the state-service has no GET-by-domain-key to
rebuild that index from after a restart.

---

## The remaining in-memory stores (full audit) and why each is blocked

Registered unconditionally in `Program.cs` (no state-service path). Each row notes the query shape
that the current contract cannot satisfy.

| Store (interface) | Reg line | Why it can't migrate today |
| --- | --- | --- |
| `IAccountDeletionStore` (`InMemoryAccountDeletionStore`) | 1361 | `AdvanceAsync` **scans all open records** + **mutates** `Scheduled→Completed`. Needs list + update. |
| `IDataExportStore` (`InMemoryDataExportStore`) | 1372 | `ClaimNextAsync` (scan+claim), `MarkReady/Failed/Delivered` (mutate), `GetByDownloadToken` (secondary-index lookup). |
| `IRefreshTokenStore` (`InMemoryRefreshTokenStore`) | 1407 | `RotateAsync`/`RevokeAsync` (mutate), `RevokeAllForUserAsync` (list-by-user), `FindByHash` (secondary index). *The security-critical durability already lands via `StateServiceRefreshFamilyWriter`.* |
| `IPendingOffersStore` (`InMemoryPendingOffersStore`) | 1490 | Auction semantics: `AcceptWithSupersede`/`TryEdit`/`TryWithdraw` (mutate under a write lock), `ListForRequest`/`WithdrawForJeeber` (list). *Routing pair already durable via `StateServiceOfferRequestIndex`.* |
| `IAvailabilityStore` (`InMemoryAvailabilityStore`) | 1528 | Presence/availability — mutable, high-churn; belongs on heartbeat/presence, not the KV. |
| `IDisputeCaseStore` (`InMemoryDisputeCaseStore`) | 1276 | `ListForUser`/`GetActiveForDelivery`/`GetByIdempotencyKey` (list + secondary indexes), `ApplyResolution`/`ReplaceEvidence`/`ApplyUnderReview` (mutate). |
| `IDisputeStore` (`InMemoryDisputeStore`) | 1256 | V1 dispute store — mutable + list; superseded by V2 + `StateServiceDisputeWriter`. |
| `IFlaggedRequestStore` (`InMemoryFlaggedRequestStore`) | 1206 | `ListAsync` (admin moderation queue, paged) + `DecideAsync` (mutate). |
| `IAdminEscalationStore` (`InMemoryAdminEscalationStore`) | 1137 | `ListAsync` + `GetForDelivery` (secondary index). |
| `IUsersStore` (`InMemoryUsersStore`) | 1345 | Read/query model mirroring user-management; not gateway-owned state — should read through `ServiceUserManagementClient`, not the KV. |
| `IProhibitedItemsStore` / `IProhibitedItemSynonymRegistry` | 1195/1204 | **Not state** — config catalogs, re-seeded on boot; correctly in-process. No migration needed. |
| `IAudioStore` (`InMemoryAudioStore`) | 1705 | Fire-and-forget retry buffer for raw audio bytes; the fallback queue is `Snapshot()`-only and nothing reads the bytes back. Pushing blobs into the opaque KV adds **zero** durability value and is an anti-pattern. No migration. |
| `IAdminAuditLog` / `ITiersStore` / notifiers / push device-token + retry + dispatch | various | Audit log (append+list), tiers (config), notifiers/transports (transient delivery state). Not opaque-row state; out of scope. |

**Net:** there is **no remaining store that is a clean create-once + GET-by-primary-key shape**
(the only shape the R1 KV supports). Every candidate needs list/scan and/or mutable update.
Partially migrating one (e.g. moving `RequestAsync`/`GetAsync` to the KV while `AdvanceAsync` keeps
mutating an in-memory copy) would **split the source of truth** and is strictly worse than today —
so we explicitly do **not** do that.

---

## Decision

1. **Do not fabricate in-gateway indexes** to work around the missing query (that would re-introduce
   the exact in-gateway state ADR-0001/0005 forbid). This is already the codebase's stance
   (`StateServiceSupportTicketStore.ListByOwnerAsync`).

2. **Unblock the remaining migrations by adding ONE generic primitive to jeeb-state-service**, then
   re-point the stores. The single primitive that unblocks the largest set is:

   **`GET /state/rows?owner={ownerId}&prefix={prefix}&limit&cursor` — list opaque state rows by
   owner / key-prefix (paged).** Paired with the existing insert-once KV, this directly closes:
   - `StateServiceSupportTicketStore.ListByOwnerAsync` (today returns empty),
   - the by-subject / by-user read-index rebuild for R2 refresh, R3 KYC, R4 ratings, R5 disputes
     (the "CONTRACT GAP" in `StateServiceWriteThroughServices.cs`),
   - `IFlaggedRequestStore.ListAsync` and `IAdminEscalationStore.ListAsync` (admin queues).

   A second primitive — **`PATCH /state/rows/{key}` (optimistic-concurrency in-place update with an
   `expectedVersion`)** — unblocks the mutable state-machine stores (`IAccountDeletionStore`,
   `IDataExportStore`, V2 `IDisputeCaseStore`, `IPendingOffersStore`). The dispute V2 endpoints
   (`TransitionDisputeAsync` with `ExpectedVersion`) already prove the state-service can do
   version-checked updates; this generalises that to the opaque KV.

3. **Stores that are NOT gateway-owned state stay out of scope:** `IUsersStore` (read-through
   user-management), `IProhibitedItems*` (config catalog), `IAudioStore` (transient retry buffer),
   notifiers/transports/audit-log. Moving these to the KV would not serve ADR-0001/0005.

### Migration order once the primitives land (highest value first)

1. **`StateServiceSupportTicketStore.ListByOwnerAsync`** — smallest, already create+get durable; only
   the list call is stubbed. One-method change, no source-of-truth split. *(do first)*
2. **R2 refresh read-index** — security value: sessions survive a gateway bounce instead of every
   user being logged out.
3. **`IFlaggedRequestStore` + `IAdminEscalationStore`** — moderation/escalation audit trail survives a bounce.
4. **R3/R4/R5 read-indexes**, then the mutable state-machine stores via the `PATCH` primitive.

---

## Consequences

- **Positive:** the gateway's remaining state surface is now fully catalogued with a per-store
  disposition and a precise, minimal upstream ask (two generic primitives, not N bespoke endpoints —
  keeping jeeb-state-service generic/reusable per the architecture invariants). No in-gateway index
  is fabricated; no source-of-truth split is introduced.
- **Negative / accepted for now:** until the `list-by-owner/prefix` primitive ships, support
  list-by-owner stays an empty cold-start page (mobile tolerates it; it does not call list today),
  and the R2–R5 read-indexes remain in-memory (their durable WRITES already land, so the
  security-critical behaviour is preserved across a bounce; only the fast read model is lost).
- **Follow-up (owned by the state-service work-stream):** ship
  `GET /state/rows?owner&prefix` (paged) and `PATCH /state/rows/{key}` (version-checked), then land
  the migration PRs in the order above. Each is a small, isolated re-point of one store behind its
  existing interface — callers are untouched.

---

## Verification performed by this audit

- Enumerated every `AddSingleton<I*Store…>` / `InMemory*` registration in `src/JeebGateway/Program.cs`.
- Read each remaining store's interface to classify its required query shape (get-by-key vs
  list/scan vs mutate vs secondary-index).
- Read the state-service NSwag client (`src/JeebGateway/Services/Clients/JeebStateServiceClient.cs`)
  to enumerate the exact operations the contract exposes (R1 idempotency, R8 locks/rate-limit, and
  the purpose-built relational writers) — confirming no list/prefix-scan or generic update exists.
- Confirmed `JeebGateway` builds clean against the available SDK (0 errors).
