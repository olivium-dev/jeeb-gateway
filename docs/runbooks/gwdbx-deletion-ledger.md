# Gateway DB Extraction — Deletion Ledger (A8/A9 appendix)

**Program**: gateway-db-extraction-2026-08-12 · **Authored**: W5 prep wave, 2026-08-14
**Contract**: PLAN v2 §7 — "a deletion-ledger appendix names the deleting wave for every table, flag,
decorator, route, job, guard entry, health check, and doc." This file is that appendix.
Companion machine-readable roster: `scripts/gwdbx-final-health-roster.txt` (A9 final roster).

Rules of the ledger:

- **A8 (step-6 teardown):** every domain ends with ONE release, after its DROP, that deletes the domain's
  mode flag, mirror/shadow decorators, legacy store, dead routes/jobs and its health/guard entries.
- **A9 (health-roster contract):** each roster-changing wave pre-announces the expected `/health/ready`
  roster — **names + count** — and the deploy checklist asserts it before the swap (precedent: the
  unexplained 19→18 that parked `6d46e69`).
- Waves below are the **deleting** wave (the release that removes the artifact), not the wave that stops
  using it. `[OWNER-GO]` marks deletions gated on an owner DROP/ruling per PLAN §6/§7.
- Status column reflects GitHub `main` + the live census at authoring time (24 domain tables remain of 37;
  see EXEC-LEDGER S3.56 §3). Later ledger sections supersede this file's status (never its contract).

---

## 1. Tables (24 domain tables + `schema_migrations`)

Already dropped (13, W0 waves — history): `ratings`, `disputes`, `kyc_submissions`,
`jeeb_cancellation_strikes`, `chat_messages`, `offers`, `delivery_financials`, `partner_wallet_operations`,
`partner_otp_challenges` (0045–0048), `saved_addresses` (0049), `notification_preferences`,
`saved_locations` (0050).

| table | deleting wave | notes |
|---|---|---|
| `users` | **W4-13** `[OWNER-GO]` | both O5 outcomes must empty GatewayPostgres (A8). (The "guard-roster edit same PR" clause is void — G-08 retired at W5-13, §6.) |
| `tiers` | **W4-14** `[OWNER-GO]` | after O4 ruling + freeze-import-flip; snapshot first |
| `delivery_requests` | **W5-08** `[OWNER-GO]` | dep: A6 precondition green (BR-9 cap guard + idempotent pre-accept cancel live in delivery-service) + O11 bug-compat test green |
| `delivery_tiers` | **W5-08** `[OWNER-GO]` | FK-held; drops only AFTER `delivery_requests` severs the FK — never early |
| `settlements` | **W2-R02** (was W5-09) | owner ruling A23 (2026-08-14) pulled the drop forward; migration `0052`, archive WAIVED |
| `settlement_enqueue` | **W2-R02** (was W5-09) | same file; zero production consumers |
| `settlement_ledger_entries` | **W2-R02** (was W5-09) | same file; W1.8 durable-backing test (B6b) retires at W2-R11 with the code |
| `settlement_batches` | **W2-R02** (was W5-09) | same file; dropped AFTER `settlements` (FK `batch_id`) |
| `admin_actions` | **W5-09** `[OWNER-GO]` | dep: W1-05 id-contract ruling (OWNER-ACTIONS A2) resolved |
| `data_exports` | **W5-09** `[OWNER-GO]` | dep: W1-08 key-semantics ruling (OWNER-ACTIONS A4) + artifact leg proven |
| `account_deletions` | **W5-09** `[OWNER-GO]` | state-service work-items authority |
| `financial_ledger_anonymization` | **W5-09** `[OWNER-GO]` | fin-anon leg lives in wallet-service (R-M4) first |
| `prohibited_items` | **W5-09** `[OWNER-GO]` | freeze-import-flip (W3-10/W3-11) done first |
| `prohibited_item_acks` | **W5-09** `[OWNER-GO]` | with `prohibited_items` |
| `flagged_requests` | **W5-09** `[OWNER-GO]` | rides state-service work-items |
| `admin_escalations` | **W5-09** `[OWNER-GO]` | delivery-service mint (`OtpEscalationsMode`) live first |
| `jeeber_availability` | **W5-09** `[OWNER-GO]` | dep: W3-13 rung 2 — currently withheld on the out-of-scope `.20` duplicate sweeper (OWNER-ACTIONS A9) |
| `device_tokens` | **W5-09** `[OWNER-GO]` | dep: W3-15 four-category push proof (gates the push-trio drop) |
| `push_retry_queue` | **W5-09** `[OWNER-GO]` | with `device_tokens` |
| `push_delivery_tracker` | **W5-09** `[OWNER-GO]` | with `device_tokens` |
| `notification_dispatch_outbox` | **W5-09** `[OWNER-GO]` | drain-and-switch: legacy dispatcher drained to zero first |
| `cms_surfaces` | **W5-09** `[OWNER-GO]` | ADR-0008: NO freeze-import precondition (bundler-service owns CMS); table verified **0 rows** 2026-08-16 |
| `cms_surface_versions` | **W5-09** `[OWNER-GO]` | with `cms_surfaces`; also **0 rows** |
| `transcription_fallback_queue` | **W5-09** `[OWNER-GO]` | A12: DELETED not migrated — drop table + enqueue write, log the fallback event |
| `schema_migrations` | ~~**W5-12**~~ **RETAINED** | **Owner directive 2026-08-16: `jeeb_gateway` is NEVER dropped.** The DROP DATABASE step and the whole pipelines phase were removed from the programme; the database is kept, unread by the gateway, with roughly 64 orphaned rows by decision. This row does not authorise a drop. |

Every DROP: G-07 archive (`pg_dump` + `sha256sum -c`) BEFORE; G-18 migration shape (table-scoped,
row-count assert, self-registering tombstone, idempotent re-apply).

**Exception, owner ruling A23 (2026-08-14), the four settlement tables only:** the G-07 archive is
WAIVED and the data loss accepted, so `0052` cites no archive path. It also carries **no row-count
abort** — that assert exists to force a review before money rows are destroyed and A23 IS that
review; keeping it would wedge every later migration on any DB still holding rows. The count is
`RAISE WARNING`ed instead so the apply log records what was destroyed.

## 2. Flags / mode enums

`scripts/gwdbx-flag-registry.txt` is the machine-readable source; its `delete=` column is normative.
Summary — deleting wave per flag (the step-6 release of its domain):

| flag | deleting wave |
|---|---|
| `CodSettlementMode`, `PayoutBatchMode`, `JeeberEarningsMode` | W5 (after W5-09 money drops) |
| `AdminAuditMode`, `DataExportMode`, `NotificationOutboxMode`, `RefreshTokenStoreMode`, `UseUpstream:FanoutWorkQueue` | W5 |
| `AccountDeletionMode`, `ProhibitedItemsMode`, `OtpEscalationsMode`, `AvailabilityMode`, `PushDispatchMode`, `CmsConfigMode` | W5 |
| `RequestsOwnerListMode`, `RequestExpiry:Source` | W5 (created W5-02/W5-04, deleted at requests step-6) |
| `TiersMode`, `UserModerationMode` | **W4** (one release after W4-14 / W4-13 drops) |
| `WalletLedgerMigration:Authority`, `WalletLedgerMigration:ShadowCompareEnabled` | **W5-10** (grandfathered; die with the WalletPostgres CS) |
| `UseUpstream:Geolocation` | W5 (after the geo-slot exit, O8-gated flip at W3-17) |
| `GatewayPostgres:ConnectionString` (DSN) | **W5-11** (A8 same-PR set) |
| `WalletPostgres:ConnectionString` (DSN) | **W5-10** |

`CmsConfigMode` additionally carries a **boot pin to `local`** from ADR-0008 (the state-service CMS leg is
superseded by bundler-service) until that W5 deletion.

**Exit gate (W5-14): the program section of the registry is EMPTY** — every `program` row deleted by its
step-6 release; `forbidden` rows (incl. `UseUpstream:Payments`, G-05) stay forever.

## 3. Decorators, shadow readers, legacy stores

| artifact | deleting wave |
|---|---|
| `PostgresJeebWalletLedgerReader` (shadow comparer; keep Null + WalletService readers) | **W5-10** |
| `MirroringAdminAuditLog` + `PostgresAdminAuditLog` | **W5-11** (A8 set, post W5-09) |
| `MirroringDataExportStore` + `PostgresDataExportStore` | **W5-11** |
| `DeliveryServiceAvailabilityMirror` collapses to sole write path; `PostgresAvailabilityStore` deleted | **W5-11** |
| `PostgresSettlementStore`, `PostgresSettlementBatchStore`, `PostgresSettlementEnqueueStore`, `PostgresSettlementLedgerClient` | **W5-11** |
| `PostgresAccountDeletionStore`, `PostgresFinancialLedger` | **W5-11** |
| `PostgresProhibitedItemsStore`, `PostgresFlaggedRequestStore` (targets: `StateServiceProhibitedItemsStore` + work-items) | **W5-11** |
| `PostgresAdminEscalationStore` | **W5-11** |
| `PostgresDeviceTokenStore`, `PostgresPushRetryQueue`, `PostgresPushDeliveryTracker` | **W5-11** |
| `PostgresNotificationDispatchOutbox` (after drain-to-zero) | **W5-11** |
| `PostgresCmsSurfaceStore` | **already deleted** from `src/` (bundler promotion, W7a); row kept for the audit trail |
| `PostgresTranscriptionFallbackQueue` + its enqueue write (A12) | **W5-09 same PR** (code + table together) |
| `PostgresTiersStore` | **W4** step-6 (one release after W4-14) |
| Users projection legacy leg inside `UpstreamBackedUsersStore` | **W4** step-6 (one release after W4-13) |
| `PostgresRequestExpiryAuthority` + local requests store legs (`DurableRequestsStore` Postgres leg) | **W5-11** (after W5-04 re-points `RequestExpiry:Source`) |
| `InMemoryLocationStore` | **W3-19** (geo-slot exit, O8-gated) |
| 43-migration folder `db/` + `db/apply.sh` + `db/seed.sh` + Npgsql package refs | **W5-11** (A8 same-PR set) |

## 4. Routes

- **Gateway public API to mobile/CMS: unchanged by design (strangler).** No gateway route deletions are
  scheduled by this program. The two owner-signed behavior exceptions (ledger-502, real door OTP) changed
  semantics, not routes.
- wallet-service legacy `/v1/jeebers/{id}/earnings` compat aliases: **already deleted** (S2.6/W2-DEPLOY,
  live at `b9e9ea0`) — recorded here because PLAN §3-B scheduled them for step-6.
- `UseUpstream:DeliveryStatusEvents` webhook re-point: **never built** (A13 deferral) — W5-05 only verifies
  the SUPERSEDED registry stamp still stands.
- Dev/test seams (`SuperLogin`, `DevEndpoints`, `TestControlPlane`): out of program scope (OWNER-ACTIONS C3).
- **W6-02 compat window (PR #457): 160 unversioned aliases ADDED, 0 routes removed.** The ten twins
  that were deliberately refused, the sharper reason the mobile pair was refused, and the one accepted
  `/admin/settlements/batches` overlap are recorded in
  [`w6-02-route-compat-window.md`](w6-02-route-compat-window.md). Read that before adding an
  unversioned route — several obvious-looking ones are already taken by a different handler.

## 5. Jobs / workers

| job | fate |
|---|---|
| `WeeklySettlementBatch` + settlement reconciler (`Financials:SettlementLedgerReconciler`) | deleted **W5-11**; settle intent moved to the delivery-service completion outbox at W2-12 (R-M3 addendum) first |
| Legacy notification-outbox dispatcher | drained to zero at W1-10 flip, deleted **W5-11** |
| Push retry-queue scanner + delivery-tracker sweep | deleted **W5-11** with the push trio |
| `AdminAuditBackfillWorker` (one-shot, ships disarmed) | deleted **W5-11** |
| Requests expiry sweeps (`Requests:Expiry:*`) local authority | re-pointed at **W5-04**, local leg deleted **W5-11** |
| `AutoOfflineSweeper` | **RETAINED** (business logic); its Postgres store leg dies at W5-11, upstream mirror becomes the only leg |
| GDPR export/deletion sweeps (`Users:DataExport:*`) | claim-worker (#417) takes dispatch when armed (OWNER-ACTIONS A3); legacy sweep deleted **W5-11** |
| `StateWorkItemClaimWorker` (#417), fan-out drain, `CourierPositionQueue` | **RETAINED** (named transients / new mechanism, hold no authoritative state) |
| `ConfigImportWorker` + `StateServiceConfigImporter` + `ConfigParityChecker` (W3-07 one-shot) | **DELETED (ADR-0010, round 2).** Their source stores are `InMemoryProhibitedItemsStore` / `InMemoryFlaggedRequestStore` — process memory that local authoring can no longer refill at `upstream-authority` — so the importer could only replay ZERO rows and parity could only compare 0 against 15. Hosted-service ratchet **19 -> 18**. `ConfigImportRun__Enabled=false` in the live drop-in becomes an unbound key; **do not delete `configimport.conf`**, it also carries the load-bearing `BUNDLER_CMS_BEARER_TOKEN_FILE`. `ILocalFlaggedRequestStore` is left vestigial and dies at W5-14 |
| bundler-service URL-group health probe | **REPLACED (ADR-0010)** by `BundlerServiceHealthCheck`: same named `HttpClient` as the data calls, bundler's own `health/ready`, non-empty body required, registered `Degraded` not `Unhealthy`. The old probe read a Host-unmatched proxy's empty 200 as `Healthy` in 2.38 ms |

## 6. Guard entries (StoreDurabilityGuard / `scripts/guard-roster.txt`) — CLOSED

**Status 2026-08-16 (W5-13): the guard machinery is retired, and the W5-11 set did not land whole.**

- G-08 is **retired**. The interim "edit the manifest in the same PR" rule is void — there is no
  manifest and no runnable gate.
- What W5-11 (`8cba63b`) actually deleted: `StoreDurabilityGuard`, `StoreDurabilityHealthCheck` and the
  `StoreDurability:FailClosedDisabled` escape hatch. What it did **not** touch:
  `scripts/guard-roster.txt` and `scripts/check-guard-roster.sh`. The manifest was left behind
  asserting 29 `Critical` durability guarantees, 14 of them naming types that no longer existed and 8
  naming no surviving implementation at all — an orphaned contract that read as live. W5-13 emptied it
  to a comment-only tombstone recording exactly that drift.
- `scripts/check-guard-roster.sh` is retired in place: it exits non-zero at its own
  `StoreDurabilityGuard.cs` existence check before reading anything, so it can neither verify nor
  regenerate. It also still requires `./db/apply.sh` / `./db/seed.sh` in `build.yml` (invariant 3,
  G-18) — `db/` went in the same commit, so that invariant can never pass either.
- The `guard-roster-gate` job in `.github/workflows/ci.yml` survives because `.github/workflows/**` is
  out of programme scope: nothing under it may be added, edited or re-enabled. Every workflow in this
  repo is disabled at the GitHub level, so the job does not run.

## 7. Health checks — the A9 roster contract

Roster **before W2-R11** (**19**, verified in `Program.cs` + `Extensions/HealthCheckExtensions.cs`):

```
admin-oidc-configuration  ban-service  cdn-service  contract-signing-service  delivery-service
form-builder-service  gateway-postgres  geolocation-service  jeeb-state-service  notification-service
offer-service  push-notification  realtime-comunication-service  store-durability  user-management
voice-transcription  wallet-postgres  wallet-service  whisper
```

Pre-announced transitions (deploy checklists MUST assert names + count before the symlink swap):

| wave | change | count |
|---|---|---|
| every wave through W2-R09 | no roster change permitted; any drift aborts the deploy | **19** |
| **W2-R11** | `settlement-service` added (Unhealthy on failure — no local fallback remains) | **20** |
| every wave W2-R11 → W5-09 | no further roster change permitted | **20** |
| **W5-10** | `wallet-postgres` removed | **19** |
| **W5-11** | `gateway-postgres` + `store-durability` removed | **17** |

**W2-R11 deploy precondition.** The probe is registered only when `Services:Settlement:BaseUrl` is set
(`AddDownstreamProbe` skips an unset BaseUrl). Set it in the env file BEFORE the symlink swap, or the
roster lands at 19 and the post-deploy assert fails.

**Readiness does NOT cover the gateway's token.** The probe dials settlement-service `/health/ready`,
which is `AllowPublic()` upstream — it never presents `Services:Settlement:ApiToken`, and the auth-config
check behind it covers settlement-service's OWN secrets, not the gateway's copy. A missing, typo'd or
under-length SERVICE-scope token therefore lands **20/20 green** while every settle 401s and is swallowed
on both completion legs. Set the ≥32-char SERVICE-scope `Services:Settlement:ApiToken` in the same env
file, and after the swap make **one authed settlement read through the gateway** (e.g.
`GET /v1/jeeb/earnings` with a minted token) — that, not readiness, is what proves the token.

The declared roster lives in code at `Extensions/GatewayHealthRoster.cs`.

**Count discrepancy — read this before asserting a number (W5-13, 2026-08-16).** Four figures for the
post-W5-11 roster are in circulation and they disagree:

| source | figure |
|---|---|
| `Extensions/GatewayHealthRoster.cs` — `ExpectedReadyCount` | **18** (15 `DownstreamProbes` + 3 `InProcessChecks`) |
| this section's transition table (below), pre-announced | 17 |
| `scripts/gwdbx-final-health-roster.txt` — "Count MUST be 17" | 17 names |
| the CI-harness paragraph below, as originally written | 16 |

The code figure is the one backed by the registration list: `bundler-service` was registered by
`HealthCheckExtensions` but never declared, so every pre-announced figure undercounted by one until
W5-11 added the declaration. `scripts/gwdbx-final-health-roster.txt` is one name short for the same
reason. It is **left unchanged here on purpose** — the probe is only registered when its BaseUrl key is
set, so whether a given deployment serves 17 or 18 names depends on live configuration this PR did not
inspect (no service was probed this round, by instruction). Reconcile it against a real
`/health/ready` before treating either number as the contract.

**FINAL roster** — machine-readable copy in `scripts/gwdbx-final-health-roster.txt`:
`admin-oidc-configuration, ban-service, cdn-service, contract-signing-service, delivery-service,
form-builder-service, geolocation-service, jeeb-state-service, notification-service, offer-service,
push-notification, realtime-comunication-service, settlement-service, user-management, voice-transcription,
wallet-service, whisper` (+ `bundler-service` when `BundlerCmsSurfaceStore.BaseUrlConfigurationKey` is set,
which is what the code declares; + `self` on the live tag, which is not part of the ready roster).

Historical note: the `gateway-postgres`/`wallet-postgres` checks and the durable-store wiring were both
gated on the DSN being present, so the W5-10/W5-11 code deletions and the env-file DSN removals had to
land in the same deploy window. Both are done; neither check exists any more.

Harness: `scripts/gwdbx-zero-dsn-smoke.sh` (workflow `zero-dsn-cold-boot.yml`) checks this contract — the
zero-DSN leg passes only when `/health/ready` serves exactly the names in
`scripts/gwdbx-final-health-roster.txt`. It is **not** enforced automatically: every workflow in this repo
is disabled at the GitHub level, so the harness only runs when someone runs it.

## 8. Docs

| doc | fate | status |
|---|---|---|
| `src/JeebGateway/contracts/SPECS-STATUS.md` (stale UPG block, "one env var arms it") | rewrite at **W5-13** | **DONE W5-13.** Whole "What was NOT removed" section replaced; there is no env var that arms UPG. |
| `db/README.md` | dies with `db/` at **W5-11** | **DONE W5-11.** `db/` is absent from the tree. |
| `docs/runbooks/db-backup-and-recovery.md` | rewritten at **W5-11** (no gateway DB left to back up) | **MISSED at W5-11, DONE W5-13.** It survived as a live-looking Postgres 16 runbook whose every script was already deleted. Now a RETIRED tombstone. |
| `docs/runbooks/gwdbx-program-rules.md` | archived at **W5-13** (rules dissolve with the program) | **DONE W5-13.** Status-stamped in place, not moved — five inbound pointers (two scripts, `scripts/gwdbx-flag-registry.txt`, `CourierPositionQueue.cs`, this file) keep resolving. A new **§0** restates the clauses that outlive the programme, G-21 first. |
| Gateway `README.md` / architecture docs | reclassified "stateless BFF/orchestrator" at **W5-13** | **DONE W5-13.** README gained a "What this service is" section: owns no database, systems-of-record table, and an explicit "What is NOT done" naming the 19 hosted services vs the allowance of 2. `WALLET-FINANCE-CUTOVER.md`, ADR-0006, ADR-0007 and the admin-tiers waiver were banner-marked in the same PR. |
| `scripts/guard-roster.txt` + `scripts/check-guard-roster.sh` | part of the W5-11 A8 set (§6) | **MISSED at W5-11, DONE W5-13.** See §6. |
| `scripts/gwdbx-flag-registry.txt` + registry gate | program rows empty by **W5-14** (exit gate); file + gate retire post-program with `forbidden` rows preserved in the `gwdbx-program-rules` archive (§0) | open |
| `scripts/check-stateless-gateway.sh` (red by design R9) | ~~flips to a GREEN required gate at W5-11~~ | **STILL RED, by design.** Its DB arms are green — the allowlist is empty and no seam exists — but the hosted-service arm pins 2 against 19 registrations and the local-store arm sees 37. It cannot go green until the remaining in-process state moves. It is also not a *required* gate: every workflow in this repo is disabled at the GitHub level. |
| this file | closed out at **W5-14** with the exit proof | open. Note the exit proof no longer includes a `DROP DATABASE` — `jeeb_gateway` is retained (§1). |

## 9. Deliberate retains (named, NEVER deleted by this program)

Edge Redis OTP rate-limiter (`RedisOtpRequestRateLimiter`); door-OTP Redis TTL keys
(`otp:attempts|lockout|handovercode`) behind the Production fail-closed Redis boot guard (A3);
`Redis:ConnectionString` (R4-sanctioned); `CourierPositionQueue` (transient back-pressure); bounded
fan-out drain buffer; `Notifications:NewRequestFanout:Enabled` kill switch; dispute-refund 400/no-op
(UPG retired, G-05).
