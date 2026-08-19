# jeeb-gateway

Jeeb BFF gateway — C#/.NET 8, NSwag-generated clients, aggregates all downstream services.

## What this service is

jeeb-gateway is a **BFF / orchestrator**. It owns **no database**. There is no
`DbContext`, no Npgsql or EntityFrameworkCore package reference, no migration
folder (`db/` is not in this repo) and no `GatewayPostgres` / `WalletPostgres`
connection string anywhere in the source. `scripts/gateway-db-seam-allowlist.txt`
holds zero entries, which makes `scripts/check-stateless-gateway.sh` reject *any*
database seam that reappears — that ratchet is absolute, not a budget.

Systems of record live in the owning services and are reached over HTTP only. The
gateway never opens another service's database (guardrail G-21):

| Domain | System of record | Gateway seam |
|---|---|---|
| Requests / deliveries | delivery-service | `Requests/UpstreamRequestsStore.cs` |
| Wallet balances + transaction ledger | wallet-service | `JeebWallet/WalletServiceJeebWalletLedgerReader` |
| Settlements / COD money rows | settlement-service | `Financials/SettlementServiceClient.cs` |
| Prohibited-items catalog | jeeb-state-service published config | `ProhibitedItems/StateServiceProhibitedItemsStore.cs` |
| Offers | offer-service | `Availability/UpstreamPendingOffersStore` |
| Identity, roles, profiles | user-management | `ServiceUserManagementClient` (generated) |
| Idempotency, locks, work-items, audit events | jeeb-state-service | `Services/Clients/JeebStateServiceClient.cs` |
| Saved locations, notification preferences | remote-user-preferences | `RemoteUserPreferences*Store` (`FeatureFlags:UseUpstream:RemoteUserPreferences` — see the flag table below; the **committed base default is `false`**) |
| Ratings | feedback-service | `Ratings/FeedbackServiceRatingStore` (`FeatureFlags:UseUpstream:Ratings`; `InMemoryRatingStore` when off) |

Four of those seams are selected by a migration flag. For three of them — every
row below except `WalletLedgerMigration:Authority` — the **committed default is
not the live value**, so read the flag, not the default, when reasoning about a
running instance:

| Flag | Committed default | Live MSI value |
|---|---|---|
| `FeatureFlags:RequestsOwnerListMode` | `local` (`Migration/GwdbxMigrationOptions.cs`; in no `appsettings*.json`) | `upstream-authority` since 2026-08-16 |
| `FeatureFlags:ProhibitedItemsMode` | `local` (same file) | `upstream-authority` |
| `WalletLedgerMigration:Authority` | `wallet-api` in `appsettings.json` **and** `appsettings.Production.json`; `postgres` in `appsettings.Development.json` | `wallet-api` |
| `FeatureFlags:UseUpstream:RemoteUserPreferences` | **`false`** — pinned at `appsettings.json:37`. The C# fallback in `Program.cs` (`GetValue(..., true)`) is `true`, but it only applies when *no* config key is present, and the base file always supplies one. `appsettings.Production.json:23` sets **`true`** | `true` wherever the Production overlay loads — read from committed config, not probed |

The wallet row is the one that bites: the WalletPostgres projection was deleted,
so anything other than `Authority=wallet-api` now resolves
`NullJeebWalletLedgerReader` and serves an **empty** ledger page rather than
falling back to a database. That is deliberate in Development (there is no wallet
DB there); it would be a silent financial-read outage anywhere else.

### What is NOT done

"Stateless" here describes **store ownership and direction of travel, not a
finished state.** The process still carries in-process state and background work,
verified on `main` at the time of writing:

- **18** exact hosted-service types and **28** transitional local-owner/queue types
  are named in `scripts/stateless-gateway-ownership-roster.txt`;
- the roster is not an exception budget: `scripts/check-stateless-gateway.sh`
  rejects any new identity and requires stale rows to be removed when ownership
  is retired, while its database/UPG arms retain zero allowance.
  Several are still the authoritative leg — `IDataExportStore`,
  `IAccountDeletionStore`, `ITiersStore` (at `TiersMode=local`),
  `IAvailabilityStore` and `IUsersStore` all resolve to a local implementation
  that a restart clears. `IFlaggedRequestStore` is **no longer** in that set
  unconditionally: since ADR-0009 it is mode-branched at
  `Program.cs:2067-2071` — `InMemoryFlaggedRequestStore` at
  `FeatureFlags:ProhibitedItemsMode=local`, `StateServiceFlaggedRequestStore`
  once the flag requires upstream (which is the live MSI value, see the flag
  table above). The in-memory leg is the fallback, not the destination.

`scripts/check-stateless-gateway.sh` is green only when that exact transitional
inventory and the zero-database boundary both hold. Do not describe this service
as fully stateless, and do not describe the gwdbx extraction programme as complete. The
`jeeb_gateway` database is **not** being dropped (owner directive 2026-08-16); it
is retained, unread by this service, with a small number of orphaned rows.

Programme record: `docs/runbooks/gwdbx-deletion-ledger.md` (what was deleted and
when) and `docs/runbooks/gwdbx-program-rules.md` (archived; §0 lists the clauses
that outlive the programme).

## Case callback deployment

`jeeb-state-service` and the gateway run on the same MSI host. Configure the
state service with:

```text
CaseManagement__GatewayCallbackUrl=http://127.0.0.1:10090/svc-callbacks/cases/events
```

The canonical callback and its backward-compatible `/v1/case-events` alias are
intentionally unauthenticated under the owner ruling against inter-service auth.
Both routes enforce `HttpContext.Connection.RemoteIpAddress` as IPv4/IPv6
loopback and reject missing or non-loopback peers, so they must not be routed
through LAN ingress or a reverse proxy.

### Support message pagination

`GET /v1/support/tickets/{id}/messages?limit=N&cursor=...` selects the newest
available page first. Items inside each page are chronological (oldest to
newest) for direct rendering. A non-null `nextCursor` walks strictly earlier
messages; clients prepend that page and continue until `nextCursor` is null.
The cursor is opaque and scoped to support messages.

### Case list sorting

The state-service case list accepts `sort=recent|sla`. `recent` is the default
and pages `(created_at, case_id)` newest-first. `sla` pages active cases first,
then due dates earliest-first with null due dates last, followed by deterministic
creation/id keys. Cursors are opaque and cannot cross sort modes. Public user
lists explicitly request `recent`; admin case queues explicitly request `sla`.

### Case and push recovery

Only an authenticated administrator with the dispute-resolution capability can
use `/admin/v1/case-recovery/*`. The gateway proxies state callback dead-letter
list/requeue operations and push-dispatch stale/get/manual-resolve operations;
it stores no recovery state. Push resolve is an operator-observed CAS command:
send the exact `version` and `updated_at` from the stale/get response as
`observed_version` and `observed_updated_at`. The gateway does not refresh those
tokens, and a fresh, terminal, or changed dispatch returns `409 Conflict`.

The generic state recovery endpoints and push operator endpoints remain on
private MSI ingress (loopback where deployed on the same host), with no service
authentication. Mobile/public clients reach only the capability-protected
gateway routes. Public dispute/support list cursors are opaque state-service
keyset cursors relayed unchanged for the same query scope.

### CDN upload ticket replay

`POST /api/cdn/assets` reserves caller idempotency keys in the existing external
state-service idempotency store, scoped by user and operation. Request-hash
collisions return `409`; successful validated tickets replay only until their
ticket expiry. Failed or invalid CDN responses are never cached as successes.

## Endpoints

### Notification preferences (T-backend-031)

- `GET  /users/me/notification-preferences` — returns the caller's per-category toggles plus the list of always-on channels.
- `PATCH /users/me/notification-preferences` — partial update of `offers`, `chat`, `statusChanges`, `ratingReminders`. Attempting to disable `otp` or `systemCritical` returns `400`.

Caller identity is taken from the `sub`/`NameIdentifier` claim, with `X-User-Id` as an MVP fallback until JWT validation is wired up.

Backed by `RemoteUserPreferencesNotificationPreferencesStore` — the generic remote-user-preferences service, storing an opaque JSON blob so the shared service learns nothing about Jeeb topics. `InMemoryNotificationPreferencesStore` survives only as the fallback when `FeatureFlags:UseUpstream:RemoteUserPreferences` resolves `false` — which is what the **committed base config does**: `appsettings.json:37` pins the key `false`, and only `appsettings.Production.json:23` sets it `true`. The `true` in `Program.cs` is the `GetValue` fallback for a *missing* key, and the base file never leaves it missing, so a deployment that does not load the Production overlay gets the in-memory store. Preferences are not a gateway-owned store and will not become one.

### Data export (T-backend-042, GDPR-like right of access)

- `POST /users/me/data-export` — body: `{ "format": "json" | "pdf" }` (default `json`). Queues a full export (profile, saved addresses, orders, ratings, chat history). Returns `202 Accepted` with `status: queued` and `dueBy = requestedAt + 72h`. Idempotent while a previous request is still open (queued / processing / ready).
- `GET  /users/me/data-export` — returns the caller's latest export record so the mobile app can poll until `status` flips to `ready` and `downloadUrl` is populated.
- `GET  /users/me/data-export/{token}/download` — single-use; the unguessable token is the credential, not the session. Returns the payload bytes (`application/json`); subsequent hits on the same token are `404`. Links expire after 7 days by default (configurable).

Backed by `InMemoryDataExportStore`, decorated by `MirroringDataExportStore`, plus the `DataExportProcessor` hosted worker. **The in-memory store is still the authoritative leg** while `FeatureFlags:DataExportMode` is `local` or `dual-write-local-read`, so a queued export and its download token do not survive a gateway restart. The durable target is jeeb-state-service work-items (`Users/DataExport/DataExportRelayPlan.cs`, runner `tools/DataExportRelay`) — **not** a gateway-owned Postgres; the gateway will not grow one. Notification fan-out (email + push) goes through `IDataExportNotifier`. The 72-hour SLA lives in `DataExportOptions.Sla`.

### Prohibited-item NLP scan (T-backend-048)

- `POST /prohibited-items/scan` — body: `{ "description": "...", "requestId": "optional" }`. Runs the active catalog through normalization → exact, synonym, and Damerau–Levenshtein fuzzy matching. Response contains the matches array, a `requiresReview` flag, and `autoBlocked: false` (always). When `requiresReview` is true the gateway records a `FlaggedRequest` entry in `IFlaggedRequestStore` and returns its id; the caller MUST NOT auto-block on the response. That store is mode-branched (`Program.cs:2067-2071`): at `FeatureFlags:ProhibitedItemsMode=local` it is `InMemoryFlaggedRequestStore` and the moderation queue is process-memory; once the flag requires upstream it is `StateServiceFlaggedRequestStore`, which writes **state-service generic cases with kind `moderation_review`** (`Program.cs:2058-2063`, ADR-0009). The durable target is that cases surface — **not** state-service work-items. The work-items leg was deleted in the same change because the importer and the read store had drifted onto different upstreams (work-items `content-flag` vs cases `moderation_review`), so the store would have read a surface nothing wrote.
- `GET  /admin/prohibited-items/flagged?status=pending|cleared|upheld&page=&pageSize=` — admin queue.
- `GET  /admin/prohibited-items/flagged/{id}` — single flagged record.
- `POST /admin/prohibited-items/flagged/{id}/decision` — body: `{ "decision": "cleared" | "upheld", "note": "..." }`.

Fuzzy thresholds are length-tiered (min token length 4, distance 1 for ≤6 chars, 2 for longer); the review score floor is 0.78. The integration suite asserts a false-positive rate below 5% on a curated benign corpus.

### Dev / test-harness endpoints (`/dev/*`) — additive, env-gated, OFF by default

A small additive surface that lets an **external** testing tool (the Jeeb E2E
test console) create REAL user-management users on demand and inspect them. It
exists only to make end-to-end scenarios reproducible; it is **not** wired into
any product flow.

**The gateway NEVER seeds data automatically.** There is no startup hook, no
`IHostedService`, no background sweeper, and no migration that seeds. A user is
created only when an explicit HTTP call hits `POST /dev/seed/user`.

#### The `[DevOnly]` annotation + the `Features:DevEndpoints` flag

- `[DevOnly]` (`Security/DevOnlyAttribute.cs`) is an `IAsyncActionFilter` applied
  at the controller-class level on `DevController`. It resolves
  `DevEndpointOptions` (`Security/DevEndpointOptions.cs`, bound from
  configuration section **`Features:DevEndpoints`**) via `IOptionsMonitor`.
- When `Features:DevEndpoints:Enabled` is **false**, every `/dev/*` route returns
  **404 Not Found** — deliberately not 403 — so the production surface is
  indistinguishable from "no such endpoint". No response body hints the route is
  real.
- The flag **defaults `false`** and is committed `false` in **every** appsettings
  file, including `appsettings.Production.json` (it MUST stay false there) — never
  committed `true`. It is read through `IOptionsMonitor` and set as a **service
  environment variable** (`Features__DevEndpoints__Enabled=true`) on the single
  environment that runs the seeding harness. Because a fresh prod never sets this
  env var, prod stays OFF (404) by default even though normal deploys now
  **preserve** the flag rather than scrubbing it (see below).

#### Toggling the flag — `dev_endpoints` workflow input (HISTORICAL MECHANISM)

> **Not operable as written (owner directive A27, 2026-08-15).** Every workflow in
> this repository is disabled at the GitHub level, and deploys go direct to MSI.
> `gh workflow run` deploys nothing today, so the recipes below describe the
> historical mechanism and the env-var semantics it produced — not a procedure you
> can follow. The `[DevOnly]` / `Features:DevEndpoints` semantics above are still
> exactly how the running service behaves. This section is left in place rather
> than rewritten because replacing it would mean documenting a deploy path this
> repo has no mandate to define; ask the owner for the current one.

The flag was controlled **exclusively** through the `dev_endpoints` input of the
vendored `.github/workflows/deploy-to-jeeb.yml` deploy workflow. The standing part of
that rule survives the workflow's retirement: do NOT commit
`Features__DevEndpoints__Enabled=true` to any appsettings file, and treat the flag as
an environment-only switch on the single environment that runs the seeding harness.

The input is **3-state**, and the default is `preserve` — **a normal deploy never
changes the `/dev/*` flag.** This fixes a footgun where the old 2-state default
(`false`) ran `--env-rm` on *every* no-arg deploy and silently disabled `/dev/*`.

```bash
# Normal deploy — does NOT touch the flag (default 'preserve'); /dev/* keeps whatever
# state it was in (prod that never armed it stays OFF; an armed env stays ON):
gh workflow run deploy-to-jeeb.yml --repo olivium-dev/jeeb-gateway --ref main \
  -f service_name=jeeb-gateway                          # dev_endpoints omitted => preserve

# Turn the /dev/* surface ON (E2E seeding environment only):
gh workflow run deploy-to-jeeb.yml --repo olivium-dev/jeeb-gateway --ref main \
  -f service_name=jeeb-gateway -f dev_endpoints=true

# Scrub it OFF (forces the 404 production surface — use only when intentionally disarming):
gh workflow run deploy-to-jeeb.yml --repo olivium-dev/jeeb-gateway --ref main \
  -f service_name=jeeb-gateway -f dev_endpoints=false
```

How the input maps onto the running service (applied on the **same** zero-downtime
`docker service update` that injects `Security__TokenMint__*`, with `stop-first` +
`--update-failure-action pause`; a failed task remains available for inspection):

| `dev_endpoints` | env mutation | `/dev/*` behaviour |
|---|---|---|
| `preserve` *(default / omitted)* | *(none — no dev-flag arg emitted)* | unchanged (persists) |
| `true` | `--env-add Features__DevEndpoints__Enabled=true` | reachable (200) |
| `false` | `--env-rm Features__DevEndpoints__Enabled` | 404 |

The deploy mutates the service **incrementally** — `docker service update --image …`
keeps every env var that the command does not explicitly `--env-add`/`--env-rm`. So
when `dev_endpoints=preserve` the workflow emits no dev-flag argument at all and the
flag's current value carries over untouched. The `true`/`false` mutations are
idempotent: `--env-add` overwrites an existing value, and `--env-rm` is a no-op when
the var is already absent, so an explicit run never errors. However the state is
verified, `GET /dev/data/users` returning `404` when off and `200` when on is the
observable that matters — the flag's value in a service env is not proof on its own.
- The whole change is additive: one new controller, one options class, one
  attribute, and three committed `false` config lines. No existing route, DTO,
  status code, or auth requirement changes.

#### Endpoints

- `POST /dev/seed/user` — creates a REAL user via the existing typed
  `ServiceUserManagementClient` (the same NSwag client `UserController`
  consumes). Body carries the tool's *semantic* fields:
  `{ role, phone, displayName, email?, password?, dateOfBirth?, runId?, tags? }`.
  The gateway maps these onto the UM `RegisterUserRequest`
  (`{ email, password, confirmPassword, username, dateOfBirth }`): `displayName`
  (+ `runId`) derives a unique upstream `username`; `email` is derived as a
  non-deliverable `seed-<runId>-<user>@jeeb.test` when omitted; a strong random
  `password` is generated when omitted (`confirmPassword` always mirrors it). The
  password is **never logged and never returned**. `role` is carried as seed
  metadata / later token claim — there is no UM role column. Returns
  `{ userId, role, phone, displayName, username, email, status, createdAt, runId, tags }`.
  Errors are RFC 7807 ProblemDetails: `400` (missing role/phone/displayName),
  `404` (flag off), upstream `4xx`/`409` passthrough on collision, `502` if UM is
  unreachable.
- `GET /dev/data/users?runId=&skip=&limit=` — read-only inspect; proxies
  `ServiceUserManagementClient.AllAsync` and shapes the result to
  `{ users[], count, source, runIdFilter }`. `runId` filters to users whose
  derived handle/email carries the run tag. Never returns passwords or tokens.
- `GET /dev/data/user/{userId}` — single-user inspect; proxies
  `ServiceUserManagementClient.ProfileAsync`, shaped like one element of the list
  view.

The dev endpoints do **not** mint tokens and do **not** accept or return the
token-mint key — minting stays on the existing `POST /auth/tokens`
(`X-Service-Auth-Key`) path. Seed → mint are two separate steps.

Tests: `tests/JeebGateway.IntegrationTests/DevEndpointsTests.cs` covers
flag-off → 404 on every dev route, and flag-on mapping/proxy behaviour (UM client
stubbed at the `HttpMessageHandler` level — no live upstream required).
