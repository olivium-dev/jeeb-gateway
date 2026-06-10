# Contracts — Acquisition Status

Snapshot of how each upstream OpenAPI spec was obtained for the initial BFF
skeleton (T-migrate-gateway-shell). "Real" specs are byte-for-byte copies of
the upstream repo's committed `swagger.json` / `openapi.json`; "placeholder"
specs are minimal valid OpenAPI 3.0 documents with zero paths and a
`x-status: PLACEHOLDER` extension. NSwag will produce an empty client from a
placeholder, which is fine — the BFF skeleton still compiles, and the first
migration ticket per service will replace it via `scripts/regenerate-clients.sh`.

| Service              | File                                | Source                                                                                  | Status            |
|----------------------|-------------------------------------|-----------------------------------------------------------------------------------------|-------------------|
| auth-service         | `auth-service.openapi.json`         | none committed upstream; service uses .NET 5 Startup-based hosting with Swashbuckle     | placeholder       |
| chat-service         | `chat-service.openapi.json`         | committed at `olivium-services/chat-service/swagger.json` (50KB, complete)              | committed-real    |
| user-management      | `user-management.openapi.json`      | committed at `olivium-services/user-management/swagger.json` (19KB, complete)           | committed-real    |
| wallet-service       | `wallet-service.openapi.json`       | none committed upstream; .NET 8 service with Swashbuckle (`Program.cs` confirmed)        | placeholder       |
| matching             | `matching.openapi.json`             | FastAPI service; no spec committed (FastAPI generates it at runtime via `/openapi.json`) | placeholder       |
| notification-service | `notification-service.openapi.json` | FastAPI service; no spec committed                                                       | placeholder       |
| geolocation-service  | `geolocation-service.openapi.json`  | FastAPI service; no spec committed                                                       | placeholder       |
| push-notification    | `push-notification.openapi.json`    | FastAPI service; no spec committed                                                       | placeholder       |
| delivery-service     | `delivery-service.openapi.json`     | committed at `olivium-services/delivery-service/docs/swagger.json` (46KB, complete)     | committed-real    |

## Filling in the placeholders

For each placeholder service the first migration ticket should:

1. Boot the upstream service locally (or point at a dev/staging instance via
   the override env var in `scripts/regenerate-clients.sh`).
2. Run `./scripts/regenerate-clients.sh` — it fetches the live spec, overwrites
   the placeholder, and regenerates the typed client.
3. Commit both `contracts/<service>.openapi.json` AND
   `Services/Generated/<Service>Client.cs` in the same commit so the contract
   diff and code diff are reviewed together.

## Why not start every service locally now?

The brief for T-migrate-gateway-shell explicitly says: "Do NOT start services
to fetch live specs — that's brittle and risky here." We respect that. The
placeholders make the BFF shell compile and run; live specs land per-service
in their migration tickets.

---

## jeeb-state-service (Layer-2 durable rewire — ADR-001-rev2)

`jeeb-state-service.openapi.json` is the LIVE spec fetched from
`http://192.168.2.50:10073/swagger/v1/swagger.json` and the
`JeebStateServiceClient.cs` is generated from it via `nswag run nswag-state.json`
(freshness-gated in CI). Probed live 2026-06-04; every endpoint exercised.

### Contract gaps found (reported, NOT worked around)

These block a *complete* R2–R7 rewire and the full bounce-survival proof. The
gateway implements durable write-through for each (writes land; the
security-critical revocation/transition/strike rows now survive a bounce), but
cannot RECONSTRUCT its in-memory query index from the state-service after a
bounce because the service is keyed by its own opaque ids:

1. **No read-by-domain-key endpoints.** `GET /v1/state/kyc/{id}`,
   `refresh-families/{familyId}`, disputes by `caseId` are keyed by the
   state-service's own GUID. The gateway queries by `userId` / `tokenHash` /
   `deliveryId` / `contextId`. Needed:
   - `GET /v1/state/refresh-families/by-hash/{tokenHash} → {subject, familyId, status}`
     (R2: reconstruct identity on refresh-after-bounce without embedding userId
     in the opaque token).
   - `GET /v1/state/kyc?subject={userId}&latest=true` (R3: latest draft after bounce).
   - `GET /v1/state/disputes?deliveryId=… | contextId=…` (R5: active-case lookup).
2. **Create/open ops do not echo the new id via the typed client.**
   `POST refresh-families`, `POST disputes`, `POST kyc` are documented `200/201`
   with no response schema, so NSwag generates `Task` (void). The gateway can't
   capture `familyId`/`caseId`/`submissionId`. Document a 201 response body.
3. **PUT /idempotency and several POSTs documented as `200` with no body.**
   Forces a read-after-write (`GET /idempotency/{key}`) to learn `inserted` and
   recover the original body. Works, but doubles the hop. Document the response
   bodies (`inserted`, `outcome`, `count`, `acquired`) in the OpenAPI so the
   typed client returns them.
4. **`POST /v1/state/cancellations/bump` returns 500 for every well-formed
   request** (probed repeatedly with date-only `windowStart`). R6 cancellation
   counter is durable-write-blocked until the server-side bug is fixed. Strikes
   (the other half of R6) work fine.

### What IS fully bounce-survivable today (1:1 contract)

- **R1 idempotency** — `PUT /idempotency` + `GET /idempotency/{key}`; the key IS
  the domain key. Replay returns the original after a gateway bounce.
- **R8 locks + rate-limit** — keyed by `lockKey` / `bucket`; 409 = held.

---

## unified-payment-gateway (UPG) — JEB-1484 boundary remediation

`unified-payment-gateway.openapi.json` is the **committed-real** de-leaked UPG
contract (OpenApiSpex `v1.1.0`), generated from the JEB-1484 branch with
`mix openapi.spec.json --spec UnifiedPaymentGatewayWeb.ApiSpec`. JEB-1484
migrated the live, product-specific COD settlement surface
(`/api/v1/payments/cod_jeeb/*`, commit dc1213e) out of the shared payments
service into the generic, product-agnostic primitive:

- `POST /api/v1/payments/settlements/record` — record a settlement keyed by
  `(source, externalRef)`; UPG performs **zero** fee/commission math (callers
  pass pre-computed `gross`/`fee`/`net`).
- `GET /api/v1/payments/settlements/{source}/{external_ref}` — read by key.

The legacy `cod_jeeb` routes are retained on UPG as deprecated forwarding
aliases (RFC 8594 Sunset headers) — intentionally **omitted** from this spec so
new gateway code only ever binds to the generic contract.

### Client + GR3 routing: WIRED behind a default-off flag

The gateway now routes the COD cash-settlement ledger post **through UPG** via a
typed client, gated by `FeatureFlags:UseUpstream:Payments` (default **false**):

- `Financials/IUpgSettlementClient.cs` + `Financials/UpgSettlementClient.cs` —
  typed transport over `POST /api/v1/payments/settlements/record`. Registered in
  `Extensions/ServiceClientExtensions.cs` with the org-standard
  bearer + `X-Service-Auth` + Polly resilience pipeline (identical to every
  other downstream client), base address from `Services:UnifiedPayment`.
- `Financials/UpgSettlementLedgerClient.cs` — an `ISettlementLedgerClient`
  implementation that MAPS the gateway-computed `gross`/`fee`/`net` onto the
  generic UPG primitive (`source="jeeb.cod"`, `externalRef=deliveryId`,
  `payeeRef=jeeberId`; Jeeb ids ride in opaque `metadata`). It performs no math.
- `Program.cs` swaps `ISettlementLedgerClient` to the UPG-backed impl only when
  the flag is on; otherwise the in-process `InMemorySettlementLedgerClient`
  stays as the instant-rollback target. Additive + non-breaking (GR1).

**GR4 — NSwag regeneration DEFERRED to CI (honest flag, not a workaround):**
`UpgSettlementClient` is a **hand-coded** interim transport because the fix host
has no `dotnet`/`nswag` toolchain. This follows the established repo precedent
for specs NSwag can't cleanly model (`Services/Clients/BanServiceClient.cs`,
`OfferServiceClient.cs`). The registry row in `scripts/regenerate-clients.sh`
pins this contract so CI/the owner can regenerate
`ServiceUnifiedPaymentGatewayClient` and swap the transport with no call-site
changes.

**Flip is owner-gated:** the flag stays OFF until UPG's owner-gated JEB-1484 PR
is approved + deployed (prod UPG is still at the pre-remediation baseline, with
no `/payments/settlements/*` surface). Enabling before then would point at a
non-existent prod endpoint — hence default-off + the deploy BLOCKER.

The gateway already owns all Jeeb settlement semantics + commission math
(`Financials/CommissionCalculator.cs`, `Financials/SettlementService.cs`), so
JEB-1484's "relocate Jeeb logic to the gateway" requirement is satisfied; this
client closes the GR3 "settlement flows THROUGH UPG" loop.
