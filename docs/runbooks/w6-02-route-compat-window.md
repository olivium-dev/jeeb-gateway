# W6-02 route compat window — what was aliased, what was REFUSED, and the one accepted overlap

**Status:** current. **Wave:** gwdbx W6-02 (serve unversioned paths alongside versioned ones),
merged as PR #457. **Judged from:** gateway `origin/main` @ `1a56711`; every line number below was
re-read on that tree, not copied from the PR body.

W6-02 added **160 unversioned route bindings** across 44 files. Every one is a routing-layer
registration only — an extra `[Route]` on a controller or an extra `[HttpX(...)]` on the *same*
action. No handler body was copied, moved, renamed or deleted, and all 181 pre-existing versioned
bindings are byte-identical before and after.

This file exists because the two most valuable results of that wave — the twins that were **not**
created, and the one overlap that was accepted with reasoning — lived only in a PR body and a commit
message. A future engineer adding an unversioned route will look here, not at PR #457.

---

## 1. The ten twins W6-02 REFUSED

Each of these unversioned paths **already has a different handler**. Binding the twin would have put
two actions on an identical verb+path: ASP.NET Core throws `AmbiguousMatchException` at *request*
time when two candidates tie on precedence, and where they do not tie it silently shadows one of
them. Neither failure shows up in a build, which is why these were reported rather than forced.

| versioned route | would have collided with | consequence |
|---|---|---|
| `POST /v1/auth/refresh` — `Auth/OtpSignIn/AuthRefreshV1Controller.cs:64` (class `[Route("v1/auth")]`) | `POST /auth/refresh` — `Controllers/AuthController.cs:35` (class `[Route("auth")]`) | two live refresh implementations on one path |
| `POST /v1/auth/logout` — `AuthRefreshV1Controller.cs:111` | `POST /auth/logout` — `Controllers/AuthController.cs:58` | as above, on logout |
| `GET /v1/disputes` — `Controllers/V1/DisputeCasesController.cs:48` | `GET /disputes` — `Controllers/DisputesController.cs:64` | case-engine list vs legacy dispute list |
| `GET /v1/disputes/{id}` — `V1/DisputeCasesController.cs:100` | `GET /disputes/{id}` — `Controllers/DisputesController.cs:121` | as above, single record |
| `GET /v1/requests` — `Controllers/V1/JeebOrdersListController.cs:73` | `GET /requests` — `Controllers/RequestsController.cs:322` (class `[Route("requests")]`) | **see §2** |
| `GET /v1/deliveries` — `V1/JeebOrdersListController.cs:199` | `GET /deliveries` — `Controllers/DeliveriesController.cs:265` (class `[Route("deliveries")]`) | **see §2** |
| `POST /v1/requests` — `V1/JeebRequestsController.cs:112` **and** `Controllers/RequestVoiceController.cs:70` (class `[Route("v1/requests")]`) | `POST /requests` — `Controllers/RequestsController.cs:77` | the versioned path is *already* double-bound (JSON vs multipart, disambiguated by `[Consumes]`); adding a third binding on the unversioned path would have collided with the legacy create |
| `GET /v1/requests/{id}` — `V1/JeebRequestsController.cs:295` | `GET /requests/{id}` — `Controllers/RequestsController.cs:358` | two get-by-id actions with different DTOs |
| `DELETE /v1/requests/{deliveryId}` — `Controllers/DeliveriesController.cs:1195` | `DELETE /requests/{requestId}` — `Controllers/RequestsController.cs:394` | delivery-cancel vs request-cancel, different semantics |
| `GET /v1/tiers` — `Controllers/V1/JeebTiersController.cs:55` | `GET /tiers` — `Controllers/TiersController.cs:42` (class `[Route("tiers")]`) | two tier catalogs |

**Consequences carried in the code:** `Controllers/V1/JeebOrdersListController.cs` and
`Controllers/V1/JeebTiersController.cs` received **no** aliases at all, and
`RequestVoiceController` received an absolute-path alias on the voice *read* only — a class-level
`[Route("requests")]` there would have manufactured the colliding `POST /requests`.

**Do not "finish the job" by adding these ten.** Each one needs the duplicate handler resolved first
(pick a winner, delete or re-path the loser); the alias is the last step, not the first.

## 2. The mobile pair (`GET /v1/requests`, `GET /v1/deliveries`) — refused for a sharper reason

These two are in the table above, but their failure mode is worse than an exception and deserves
separate billing. `V1/JeebOrdersListController` and the legacy `RequestsController` /
`DeliveriesController` list actions return **different DTOs from different actions**. A replay onto
the unversioned path would not necessarily have thrown — it would have handed the jeeber
order-history screen a differently shaped body from a handler nobody intended to call, with a `200`
on it. A silent wrong shape in a list screen is far harder to trace than a boot crash or a `500`.

The same reasoning is why jeeb-mobile's own W6-02 change (mobile PR #257) is a **404/405 fallback
interceptor** that explicitly refuses to replay these two paths, rather than a blanket rewrite.

## 3. ACCEPTED overlap — `GET /admin/settlements/batches` vs `GET /admin/settlements/{settlementId}`

**This is accepted, not a defect, and not something to "fix" by deleting an alias.**

| | route | source |
|---|---|---|
| literal | `GET /admin/settlements/batches` | `Controllers/AdminSettlementsController.cs:31` (`[HttpGet("batches")]` under class `[Route("admin/settlements")]`) |
| parameter | `GET /admin/settlements/{settlementId}` | `Controllers/AdminCodSettlementsController.cs:65` |

**The overlap exists only on the unversioned tree.** The two versioned originals live under
*different prefixes* — `v1/admin/settlements/batches` (`AdminSettlementsController.cs:17`) and
`admin/v1/settlements/{settlementId}` (`AdminCodSettlementsController.cs:64`). Collapsing `v1/admin/*`
and `admin/v1/*` onto one `admin/*` root is what brings them adjacent.

**Why it is accepted:**

1. **Behaviour is deterministic.** ASP.NET Core endpoint routing ranks a literal segment above a
   parameter segment, so `/admin/settlements/batches` always selects `ListBatches`. There is no tie,
   therefore no `AmbiguousMatchException` — this is the ranked case, not the ambiguous one.
2. **The blast radius is one impossible id.** The only reachability loss is a settlement whose id is
   *literally* the string `batches`.
3. **Even that id is not lost** — it stays reachable at `GET /admin/v1/settlements/batches`, which is
   untouched. The loss is confined to the alias.
4. **Nothing is shadowed in the other direction.** `ListBatches` is a `503` "moved to
   settlement-service" stub (gwdbx W2-R11); it is not serving data that the detail route would
   otherwise return.

**Clean fix — a W6-04-window candidate, not a now-change.** Rename the *alias* to
`GET /admin/cod-settlements/{settlementId}` so the COD detail surface stops sharing a root with the
batch surface. It is deliberately deferred because renaming an unversioned admin path is a client
break for jeeb-cms, and the compat window's whole premise is that unversioned paths stay stable
until W6-04 (mobile migration + forced-upgrade gate) releases the fleet. Do it in that window, with
jeeb-cms's caller list in the same change — not before.

## 4. Fleet context for this wave

W6-02 shipped across seven repos. The gateway is the largest single contributor (160 aliases). One
count in the programme record needs correcting here so it is not repeated: **bundler-service PR #6
added 16 aliases, not 17.** `internal/router/router.go` mounts `registerAPIRoutes` once per entry in
`apiPrefixes = {"/api/v1", "/api"}`, and that function binds exactly 16 routes (8 under `/bundles`,
8 under `/namespaces/:namespace/documents`). The second prefix therefore contributes 16 aliases, for
38 routes on the engine in total once `/health/*` (3), `/metrics`, `/swagger/*any` and `/` are
counted. Verified by reading the router source at bundler `origin/main` `29cb21f`; the fleet
dashboard's "17 via a prefix loop" was one high.

## 5. Related

- `docs/runbooks/gwdbx-deletion-ledger.md` §4 (Routes) — the strangler rule that no gateway route is
  deleted by the extraction programme.
- Deploy ordering: a gateway build carrying these aliases also *dials* unversioned paths on
  delivery-service, jeeb-state-service and bundler-service, so those must be deployed first. That
  constraint is owner-tracked (OA-18); it is not a route-table property and is not restated here.
