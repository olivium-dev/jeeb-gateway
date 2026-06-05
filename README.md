# jeeb-gateway
Jeeb BFF gateway — C#/.NET 8, NSwag-generated clients, aggregates all downstream services.

## Endpoints

### Notification preferences (T-backend-031)

- `GET  /users/me/notification-preferences` — returns the caller's per-category toggles plus the list of always-on channels.
- `PATCH /users/me/notification-preferences` — partial update of `offers`, `chat`, `statusChanges`, `ratingReminders`. Attempting to disable `otp` or `systemCritical` returns `400`.

Caller identity is taken from the `sub`/`NameIdentifier` claim, with `X-User-Id` as an MVP fallback until JWT validation is wired up.

Backed by `InMemoryNotificationPreferencesStore` for the MVP. Production wiring will replace it with an NSwag-generated client for the notification-service preferences endpoints.

### Data export (T-backend-042, GDPR-like right of access)

- `POST /users/me/data-export` — body: `{ "format": "json" | "pdf" }` (default `json`). Queues a full export (profile, saved addresses, orders, ratings, chat history). Returns `202 Accepted` with `status: queued` and `dueBy = requestedAt + 72h`. Idempotent while a previous request is still open (queued / processing / ready).
- `GET  /users/me/data-export` — returns the caller's latest export record so the mobile app can poll until `status` flips to `ready` and `downloadUrl` is populated.
- `GET  /users/me/data-export/{token}/download` — single-use; the unguessable token is the credential, not the session. Returns the payload bytes (`application/json`); subsequent hits on the same token are `404`. Links expire after 7 days by default (configurable).

Backed by `InMemoryDataExportStore` + the `DataExportProcessor` hosted worker for the MVP. Notification fan-out (email + push) goes through `IDataExportNotifier`; production wiring will replace the in-memory implementations with Postgres-backed equivalents and an NSwag-generated notification-service client. The 72-hour SLA lives in `DataExportOptions.Sla`.

### Prohibited-item NLP scan (T-backend-048)

- `POST /prohibited-items/scan` — body: `{ "description": "...", "requestId": "optional" }`. Runs the active catalog through normalization → exact, synonym, and Damerau–Levenshtein fuzzy matching. Response contains the matches array, a `requiresReview` flag, and `autoBlocked: false` (always). When `requiresReview` is true the gateway records a `FlaggedRequest` row and returns its id; the caller MUST NOT auto-block on the response.
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
  file, including `appsettings.Production.json` (it MUST stay false there). It is
  flipped on **only** via the environment variable
  `Features__DevEndpoints__Enabled=true` in the single environment that runs the
  seeding harness — never committed `true`. Because it is read through
  `IOptionsMonitor`, a config reload toggles it without a redeploy.
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
