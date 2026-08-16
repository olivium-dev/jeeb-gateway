# ADR-0010 — Retire the config import/parity trio; the moderation cold-start 503 is deliberate

Status: **ACCEPTED** (gwdbx LOOP round 2, 2026-08-16)
Supersedes the "owner-gated / not done in this PR" note in ADR-0008 §Consequences.

Three findings from the round-2 assessment are answered here: the parity checker that can never report
clean, the moderation cold-start window, and the bundler health probe that could not fail.

---

## 1. `ConfigImportWorker` / `StateServiceConfigImporter` / `ConfigParityChecker` are DELETED

### The reported defect

`ConfigParityChecker` compares `ILocalProhibitedItemsStore` against the published upstream surface.
Reading the LOCAL store is deliberate — its own header says comparing the serving interface would
"compare upstream with itself and report clean by construction". But post-flip
(`FeatureFlags:ProhibitedItemsMode=upstream-authority`, live) `DefaultLexiconSeeder` self-skips, so
local holds 0 items against 15 upstream and every upstream item reports as "not active locally". The
check is structurally unable to report clean.

### Why retirement beats making it mode-aware

Making the lexicon leg mode-aware (skip or invert it when `RequiresUpstream(ProhibitedItems)`) would
silence the noise but leave a worker whose remaining job is to compare an empty set against nothing.
The deeper fact, verified on main:

- `ILocalProhibitedItemsStore` resolves to `InMemoryProhibitedItemsStore` and
  `ILocalFlaggedRequestStore` to `InMemoryFlaggedRequestStore` (`Program.cs`). Both are **process
  memory**, wiped on every bounce.
- From the read rung up, local catalog authoring **fails closed** —
  `StateServiceProhibitedItemsStore.CreateAsync`/`UpdateAsync` throw
  `OwnerCapabilityUnavailableException` — and `DefaultLexiconSeeder` returns early.

So the importer's *source* is a store that is empty at boot and cannot be refilled at the live rung.
The importer can only ever replay **zero rows**, and the checker can only ever compare **zero against
fifteen**. This is not "work that is finished"; it is work that is no longer expressible. A mode-aware
checker would be a green no-op — the exact failure shape ADR-0008 §2 refused for `CmsConfigMode`.

### Adversarial dependency check (what breaks)

| Depends on the deleted types | Disposition |
| --- | --- |
| `StateServiceConfigImporter.Application` const | Referenced only by `ConfigParityChecker` (deleted with it) and tests. The live read path has its **own** identical `StateServiceProhibitedItemsStore.Application = "jeeb-gateway"`. **Nothing breaks.** |
| `Program.cs` DI (`ConfigImportRunOptions`, `ConfigParityChecker`, `AddHostedService<ConfigImportWorker>`, `AddTransient<StateServiceConfigImporter>`) | Removed here. |
| `ConfigImportRun__Enabled=false` in the live drop-in | Becomes an unbound configuration key — .NET ignores unknown keys. **Do not delete `configimport.conf`**: it also carries the load-bearing `BUNDLER_CMS_BEARER_TOKEN_FILE`. |
| `ConfigImportPrepW307Tests.cs` | Deleted — every case in it exercises the worker/importer/checker. |
| `StateServiceConfigW303Tests.cs` | The five `Import_*` cases and their fixtures are removed; the read-seam, ladder-default and `StoreDurabilityGuard` cases are untouched. |
| `CmsConfigLegSupersededTests.cs` reflection guard (no `ICmsSurfaceStore` parameter) | Replaced with the **stronger** assertion that the types do not exist in the assembly at all. |
| `StateConfigContracts.cs` (same namespace) | Survives — it holds the `IStateConfigClient` DTOs the live read path uses. The namespace is not emptied, so every `using JeebGateway.StateService.Config;` stays valid. |
| `ILocalFlaggedRequestStore` | Its last consumers were the importer and the checker. Left in place as a **vestigial** marker interface (its registration is inert); it dies with the program section at W5-14. |
| Rollback ("what if we need to re-import?") | There is nothing to re-import *from*: the source stores are process memory and authoring fails closed. A future re-import would read from upstream, which is a different program. |

### Ratchet

Hosted-service registrations **19 -> 18**. `scripts/check-stateless-gateway.sh` stays red (allowance 2)
but is one closer.

---

## 2. Moderation cold start: the 503 is the design, and it stays

### The residual gap

PR #460 gave `StateServiceProhibitedItemsStore` a last-known-good cache, so a state-service blip after
the first successful lexicon read keeps the create-time gate serving. The LKG is **empty from boot**,
so a blip inside that window still fails `ModerationGate` closed and 503s request creation.

### Decision: accept the 503, document it, and warm the cache from more paths

Rejected — **seed a local floor.** This programme already recorded "the box was moderating against 4
terms instead of the 15 that were published" as a LIVE REGRESSION (`prohibiteditems-flip.conf`). A
floor makes the gate *look* healthy while enforcing a strict subset of the published lexicon: prohibited
items pass, no alarm fires, and the failure is invisible until someone audits. Explicit unavailability
is strictly safer than silent partial enforcement on a *moderation* gate.

Rejected — **warm at boot.** The only mechanism is a startup `IHostedService`, which the ratchet forbids
(and which would trade an honest per-request 503 for a boot-time dependency on state-service).

Accepted — the window stays open **on purpose** and is named honestly: the failure surfaces as
`OwnerCapabilityUnavailableException("jeeb-state-service published moderation lexicon (no cached
snapshot, local lexicon empty)")`, not as a silent allow. Only request *creation* is affected; the rest
of the gateway serves normally.

The window is narrowed, not closed, by one change made here: `ReadAllOrThrowAsync` (the admin catalog
list and `GetAsync`) now populates the LKG on any successful published read. Previously only
`ListActiveAsync` did, so an admin opening the catalog left the create-time gate cold.

### Operator note

If the gateway is restarted while state-service is unavailable, `POST /requests` answers 503 until the
first successful lexicon read. That is correct fail-closed behaviour. The remedy is to restore
state-service, not to seed terms into the gateway.

---

## 3. The bundler probe can now fail

`HealthCheckExtensions` registered bundler-service as a URL-group probe against
`${BUNDLER_CMS_BASE_URL}health/live`. Verified live 2026-08-16: `http://192.168.2.39:10056/` answers
`200` with `Content-Length: 0` for **every** path (no Caddy site block matches that Host), while
`http://127.0.0.1:10056/health/ready` answers `503`. `/health/aggregate` therefore reported
`"bundler-service":"Healthy"` in 2.38 ms against a dead upstream.

`BundlerServiceHealthCheck` replaces it: same named `HttpClient` as the data calls, bundler's own
`health/ready`, and a **non-empty body** requirement so a proxy default can no longer read as Healthy.
It is registered `Degraded` (was `Unhealthy`) — an admin-only authoring plane must be visible in
`failing[]` without 503-ing `/health/ready` for the whole gateway, the same treatment cdn-service and
form-builder-service already get.

**Consequence to expect at the next deploy:** once the owner points `BUNDLER_CMS_BASE_URL` at
`127.0.0.1`, `/health/aggregate` will report `"status":"Degraded"` with `bundler-service` in
`failing[]`. That is the probe working. bundler's database still points at the decommissioned
`192.168.2.20`; rehoming it is an owner action outside this programme.

## Guardrails checked

Gateway stays stateless (three types deleted, no store added); **no hosted service added — one
retired** (19 -> 18); no cross-service DSN read (the probe is HTTP to the service's own health route);
no breaking change to a reusable service (bundler-service and state-service are untouched — only the
gateway's opinion of them changed); no live config, systemd unit or workflow touched.

---

## 4. Regression cover (round 2, branch `gwdbx/r2-config-legs`)

The three decisions above landed without tests. Round 2 wrote cover for both behavioural claims --
but wrote it into `tests/JeebGateway.IntegrationTests`, **which has not compiled since W5-11**
(74 errors, byte-identical at the pre-round-2 SHA). See section 5: the claims below were pinned in
text and dead in practice until round 3 ported the standalone ones. Each is invisible to the
obvious check:

- `tests/.../Cms/BundlerServiceHealthCheckTests.cs` — an empty `200` is **not** Healthy (with a
  negative control asserting the status line alone reads as success, which is exactly why the old
  URL-group probe passed), `503` is not Healthy, a non-empty `200` is Healthy, the probe dials
  `health/ready` and not `health/live`, and the registration resolves to `BundlerServiceHealthCheck`
  at `Degraded` with the roster declaring the same name.
- `tests/.../ProhibitedItems/StateServiceConfigW303Tests.cs` — an admin catalog read warms the
  last-known-good snapshot so a later state-service blip serves it instead of 503-ing (the narrowing
  in §2), and a genuinely cold start with an empty local lexicon throws
  `OwnerCapabilityUnavailableException` naming `no cached snapshot` (the accepted 503 in §2).

### Also removed here

The `StoreDurabilityGuard` case in `StateServiceConfigW303Tests.cs` referenced
`StoreDurabilityGuard` and `PostgresProhibitedItemsStore`, **both deleted at W5-11 (`8cba63b`)**. It
could not compile. See the note in `docs/runbooks/gwdbx-program-rules.md` §0 — six other compiled
test files still carry the same dangling reference, and fixing those is a separate decision because
it removes durability-guard coverage.

---

## 5. Correction (round 3, branch `gwdbx/r3-executable-guards`)

Section 4 overstated the position. `tests/JeebGateway.IntegrationTests` fails to compile (2x CS0234,
68x CS0246, 4x CS0535) and has since W5-11, so every guard section 4 lists was written but never
executed; `tests/JeebGateway.UnitTests` compiles and runs, and held 3 tests. R3-3 ported the guards
that stand without a host into `tests/JeebGateway.UnitTests`:

- `HostedServiceRetirementGuardTests.cs` -- the freeze-import trio is absent from the gateway
  assembly by FQN, absent by simple name in **any** namespace (catches a resurrection under a
  rename), and no `IHostedService` implementation remains in `JeebGateway.StateService.Config`.
  Each assertion carries an anti-vacuity control, so an emptied or renamed namespace fails rather
  than passes.
- `BundlerServiceHealthCheckFailClosedTests.cs` -- empty `200`, non-2xx and transport failure each
  report the **registered** `FailureStatus` (theory over `Degraded` and `Unhealthy`, so a hardcoded
  status fails), a non-empty `200` is Healthy whatever the registration says, and the probe dials
  the store's named client on `health/ready`.

Not ported: the hosted-service ratchet. Its real gate counts `AddHostedService` **source text** in
`scripts/check-stateless-gateway.sh`; a reflection count of `IHostedService` implementations is a
different measurement and would stay green while a new registration of an existing type slipped
through. The `19 -> 18` claim under "Guardrails checked" above remains script-enforced only.

Still not ported (needs a host): the `Every_Rung_Above_Local_Refuses_To_Boot` options-validation
cases, the `ICmsSurfaceStore` -> `BundlerCmsSurfaceStore` composition case, the health-check
registration/roster case, and the whole W303 cold-start suite. Those stay dead until the
integration project compiles.
