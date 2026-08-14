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
| `users` | **W4-13** `[OWNER-GO]` | both O5 outcomes must empty GatewayPostgres (A8); guard-roster edit same PR |
| `tiers` | **W4-14** `[OWNER-GO]` | after O4 ruling + freeze-import-flip; snapshot first |
| `delivery_requests` | **W5-08** `[OWNER-GO]` | dep: A6 precondition green (BR-9 cap guard + idempotent pre-accept cancel live in delivery-service) + O11 bug-compat test green |
| `delivery_tiers` | **W5-08** `[OWNER-GO]` | FK-held; drops only AFTER `delivery_requests` severs the FK — never early |
| `settlements` | **W5-09** `[OWNER-GO]` | dep: R-M3 settle-intent relocated (W2-12) + G-06 wallet-side anonymization |
| `settlement_enqueue` | **W5-09** `[OWNER-GO]` | same dep as `settlements` |
| `settlement_ledger_entries` | **W5-09** `[OWNER-GO]` | same dep; W1.8 durable-backing test (B6b) retires in the same PR |
| `settlement_batches` | **W5-09** `[OWNER-GO]` | dep: W2-09 `paidAt`/clearing ruling + W2-10 backfill parity |
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
| `cms_surfaces` | **W5-09** `[OWNER-GO]` | freeze-import-flip done first |
| `cms_surface_versions` | **W5-09** `[OWNER-GO]` | with `cms_surfaces` |
| `transcription_fallback_queue` | **W5-09** `[OWNER-GO]` | A12: DELETED not migrated — drop table + enqueue write, log the fallback event |
| `schema_migrations` | **W5-12** `[OWNER-GO]` | deliberate retain until the end; dies with `DROP DATABASE jeeb_gateway` |

Every DROP: G-07 archive (`pg_dump` + `sha256sum -c`) BEFORE; G-18 migration shape (table-scoped,
row-count assert, self-registering tombstone, idempotent re-apply).

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
| `PostgresCmsSurfaceStore` | **W5-11** |
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

## 6. Guard entries (StoreDurabilityGuard / `scripts/guard-roster.txt`)

- Interim rule unchanged (G-08): any PR changing a store edits the manifest **in the same PR**;
  `guard-roster-gate` enforces.
- Each roster line pointing at a `Postgres*` implementation is rewritten to its upstream implementation at
  its domain's flip, and **deleted** when the interface itself retires. Waves: tiers/users lines at **W4**
  step-6; every remaining line's Postgres mapping at **W5-11**.
- **The guard machinery itself** — `StoreDurabilityGuard`, `StoreDurabilityHealthCheck`,
  `scripts/guard-roster.txt`, `scripts/check-guard-roster.sh`, the `guard-roster-gate` CI job and the
  `StoreDurability:FailClosedDisabled` escape hatch — is deleted at **W5-11** (A8 same-PR set). Rationale:
  once no local durable store exists, "critical store resolved to in-memory" is unrepresentable.

## 7. Health checks — the A9 roster contract

Current live roster (**19**, verified in `Program.cs` + `Extensions/HealthCheckExtensions.cs`):

```
admin-oidc-configuration  ban-service  cdn-service  contract-signing-service  delivery-service
form-builder-service  gateway-postgres  geolocation-service  jeeb-state-service  notification-service
offer-service  push-notification  realtime-comunication-service  store-durability  user-management
voice-transcription  wallet-postgres  wallet-service  whisper
```

Pre-announced transitions (deploy checklists MUST assert names + count before the symlink swap):

| wave | change | count |
|---|---|---|
| every wave through W5-09 | no roster change permitted; any drift aborts the deploy | **19** |
| **W5-10** | `wallet-postgres` removed | **18** |
| **W5-11** | `gateway-postgres` + `store-durability` removed | **16** |

**FINAL roster (16)** — machine-readable copy in `scripts/gwdbx-final-health-roster.txt`:
`admin-oidc-configuration, ban-service, cdn-service, contract-signing-service, delivery-service,
form-builder-service, geolocation-service, jeeb-state-service, notification-service, offer-service,
push-notification, realtime-comunication-service, user-management, voice-transcription, wallet-service,
whisper` (+ `self` on the live tag, which is not part of the ready roster).

Note: the `gateway-postgres`/`wallet-postgres` checks and the durable-store wiring are both gated on the
DSN being present, so the W5-10/W5-11 code deletions and the env-file DSN removals must land in the same
deploy window or the roster assert fails — that is the assert doing its job, not a flake.

## 8. Docs

| doc | fate |
|---|---|
| `src/JeebGateway/contracts/SPECS-STATUS.md:118-135` (stale UPG block, "one env var arms it") | rewrite at **W5-13** (known-stale since S3.56) |
| `db/README.md` | dies with `db/` at **W5-11** |
| `docs/runbooks/db-backup-and-recovery.md` | rewritten at **W5-11** (no gateway DB left to back up) |
| `docs/runbooks/gwdbx-program-rules.md` | archived at **W5-13** (rules dissolve with the program) |
| Gateway `README.md` / architecture docs | reclassified "stateless BFF/orchestrator" at **W5-13** |
| `scripts/gwdbx-flag-registry.txt` + registry gate | program rows empty by **W5-14** (exit gate); file + gate retire post-program with `forbidden` rows preserved in `gwdbx-program-rules` archive |
| `scripts/check-stateless-gateway.sh` (red by design R9) | flips to a GREEN required gate at **W5-11**; stays forever |
| this file | closed out at **W5-14** with the exit proof (zero-DSN cold boot + two-replica proof) |

## 9. Deliberate retains (named, NEVER deleted by this program)

Edge Redis OTP rate-limiter (`RedisOtpRequestRateLimiter`); door-OTP Redis TTL keys
(`otp:attempts|lockout|handovercode`) behind the Production fail-closed Redis boot guard (A3);
`Redis:ConnectionString` (R4-sanctioned); `CourierPositionQueue` (transient back-pressure); bounded
fan-out drain buffer; `Notifications:NewRequestFanout:Enabled` kill switch; dispute-refund 400/no-op
(UPG retired, G-05).
