# Gateway DB Extraction — Program Rules (gwdbx) — ARCHIVED

**Status:** ARCHIVED at W5-13, 2026-08-16. Historical record of how the
gateway-db-extraction-2026-08-12 program was run. It is no longer a live rule set — **except for §0**.
**Service**: jeeb-gateway · **Program**: gateway-db-extraction-2026-08-12 (closed to new waves)
**Still-binding clauses**: §0 below. **Deletion record**: `docs/runbooks/gwdbx-deletion-ledger.md`.
**Plan / playbook / artifacts**: `jeeb/PLAN-…`, `jeeb/PLAYBOOK-…`, `jeeb/_artifacts/gw-db-extraction-20260812/`
— these live in the owner's workspace, **not in this repository**, so they cannot be resolved from a clone.

Why this file is archived: its premise — "the gateway still owns tables, so these rules apply to every
PR" — stopped being true. The `GatewayPostgres` and `WalletPostgres` seams are deleted (PRs #445 / #446 /
#448), the `db/` migration folder is gone, `scripts/gateway-db-seam-allowlist.txt` holds zero entries,
prohibited-items config is served by state-service (`FeatureFlags:ProhibitedItemsMode=upstream-authority`
live), and delivery-service is the system of record for requests, which the gateway reads and writes over
HTTP (`FeatureFlags:RequestsOwnerListMode=upstream-authority`, live 2026-08-16). The gateway owns no table.

What is NOT finished, so nobody reads this stamp as "done": `Program.cs` and
`Extensions/BffServiceCollectionExtensions.cs` register **19** hosted services against the
`check-stateless-gateway.sh` allowance of **2**, and 37 local-store singletons remain — the gateway is not
yet stateless. `jeeb_gateway` the database is **never being dropped** (owner directive 2026-08-16); it is
retained with roughly 64 orphaned rows by decision.

Read everything below §0 as history. Do not cite a wave number, a gate outcome, an enforcement claim or an
"arm it like this" recipe from it as current fact — several are already false, and each section says so
where it is wrong.

## 0. Clauses that OUTLIVE the program (still binding)

Archiving this file must not retire these. They are restated here because live code and scripts cite them
and this document is their only definition in the repo:

- **G-21 backfill standard** — §2 items 1–3 below, verbatim and unchanged: an idempotent HTTP
  ingest/import endpoint in the OWNING service; **no cross-service DSN reads, ever**; dryRun-capable and
  re-runnable so a double run is a no-op. Cited from `src/JeebGateway/Requests/UpstreamRequestsStore.cs`,
  `src/JeebGateway/StateService/Config/StateServiceConfigImporter.cs`,
  `tests/JeebGateway.IntegrationTests/ProhibitedItems/StateServiceConfigW303Tests.cs` and
  `scripts/gwdbx/*.sh`. Item 4 (who runs it) is history; the shape rules are not.
- **The never-do list** — §1 in full: never re-add `UseUpstream:Payments` (G-05), never revert PR #385
  (G-05), never merge `codex/ops-gateway-residue-20260810-wip` (G-05), no jeeb vocabulary in
  notification-service (G-25), no new repos and no new deployables (G-26), and never re-add the
  CMS → state-service config leg (ADR-0008, 2026-08-16 — `FeatureFlags:CmsConfigMode` is pinned to
  `local` at boot). §1 is the live list: a clause added there is binding even though the rest of this
  file is history.
- **`CourierPositionQueue` disposition** — §3: a transient in-proc buffer, never a durable store. Cited
  from `src/JeebGateway/Realtime/CourierPositionQueue.cs:43-44`.
- **Two ratchets** — `scripts/gateway-db-seam-allowlist.txt` is empty and may never gain a line; the 19
  hosted registrations may never increase.
- **The forbidden flag names** — §4.1 plus `scripts/gwdbx-flag-registry.txt`: the SUPERSEDED/retired keys
  stay inactive permanently, including after the registry gate itself retires (deletion ledger §8).

## 1. Never-do list (PRE-06)

- **Never merge `codex/ops-gateway-residue-20260810-wip`.** That branch is frozen and broken; the only sanctioned way to
  take anything from it is a **cherry-pick of `6d46e69`** (the wallet-ledger lineage). — G-05
- **Never revert gateway PR #385.** It carried the `6d46e69` lineage at `66a7b9d` (the live SHA when this
  file was written, 2026-08-12). What survives on main today is `WalletServiceJeebWalletLedgerReader` plus
  the `WalletLedgerMigration` options — now the ONLY ledger source, since W5-10 deleted the WalletPostgres
  half. Reverting it would leave no reader at all. — G-05
- **UPG is retired — never re-add `UseUpstream:Payments`** (or a `"Payments"` upstream key). The key was deleted
  2026-07-27; the surviving mentions are tombstone comments/docs. Dispute-refund keeps returning 400/no-op. — G-05, PLAN §8
  The env spelling `FeatureFlags__UseUpstream__Payments` outlived the repo deletion in the live `gateway.env`
  (value `false`, zero read sites, so inert) until 2026-08-14; deleted there, and the flag-registry gate's D3
  deploy-env arm rejects the env spelling too **when `scripts/check-gwdbx-flag-registry.sh` is run by hand**.
  Nothing runs it automatically. — G-05-PAYMENTS-KEY-RESIDUE
- **notification-service is never a target of this program (R7).** No PRs, no jeeb vocabulary there. The gateway calls only
  its existing generic surfaces (`ServiceNotificationClient`, `JeebNotificationRecordClient`); the outbox goes to
  state-service work-items + push-notification instead. — G-25
- **No new repos and no new deployables** — every domain lands on an already-existing fleet service. — G-26
- **Never re-add the CMS -> state-service config leg.** ADR-0008 (2026-08-16) ruled it SUPERSEDED:
  bundler-service owns every surface/draft/publication row, the gateway owns none, and the leg's
  dependency (`192.168.2.20`) and source rows are both permanently gone. `FeatureFlags:CmsConfigMode` is
  PINNED to `local` at boot, and neither `StateServiceConfigImporter` nor `ConfigParityChecker` may grow
  a CMS leg again — replaying bundler documents into state-service forks the catalog into two writable
  owners with no reconciler.

## 2. Backfill standard (PRE-07 — rider R2, guardrail G-21)

**Items 1–3 survive this file's archiving (§0).** Every backfill in this program had — and any future
gateway backfill still has — the same shape. No exceptions, no "just this once":

1. It consumes an **idempotent HTTP ingest/import endpoint in the OWNING service** — or it is a gateway-resident replay
   job that POSTs to that target's ingest route. The code lives in the owning service or in the gateway; never in a
   standalone tool that talks to someone else's database.
2. **No cross-service DSN reads — ever.** A backfill must not add a `ConnectionString`/DSN pointing at another service's
   database: that recreates exactly the coupling this program deletes.
3. It is **dryRun-capable and re-runnable** end to end — a double run is a no-op.
4. ~~The **owner runs it**.~~ Then superseded by owner grant A19 (2026-08-13, EXEC-LEDGER S3.41), which
   authorised backfills and deploys without a per-run approval. **HISTORY — do not read this as a standing
   authorisation.** Later owner directives narrowed it: A27 (2026-08-15) restricted deploys to direct-to-MSI,
   and rounds since have prohibited running backfills, builds and deploys outright. Two scripts in this repo
   still carry the older, stricter spelling (`scripts/gwdbx/w4-05-users-moderation-backfill.sh` and
   `w4-10-tiers-backfill.sh`, both `# OWNER-RUN (G-21)`). Ask the owner; do not infer permission from here.
   Shape rules 1–3 are unchanged and remain binding (§0).

### 2.1 W1-04 — the admin-audit relay

> **Retired code — read this as a worked example, not a procedure.** `AdminAuditBackfillWorker` and its
> `AdminAuditBackfill:*` options no longer exist under `src/`; the arming block below cannot be run against
> main, and the gateway has no DSN with which to run the `SELECT count(*)` exit check. The
> `AdminAuditBackfill:BatchSize` / `:DryRun` rows still in `scripts/gwdbx-flag-registry.txt` are orphaned
> registry entries for deleted code — G-22 containment is one-way, so they do not fail the gate.

`AdminAuditBackfillWorker` replays every `admin_actions` row to state-service `POST /v1/audit-events` under
`Idempotency-Key = admin_actions.id` (G-15), the same key `MirroringAdminAuditLog` uses, so a row the live mirror
already wrote replays instead of duplicating. Both bodies come from `AdminAuditEventMapping`, because the upstream
unique key is `(application, idempotency_key)` and a drifting body would be rejected with 409 rather than reconciled.

Arm it per run, never by deploying it — `AdminAuditBackfill:Enabled` is `false` by default and `:DryRun` is `true`:

```
AdminAuditBackfill__Enabled=true  AdminAuditBackfill__DryRun=true   # rehearse: read + log, POST nothing
AdminAuditBackfill__Enabled=true  AdminAuditBackfill__DryRun=false  # relay
AdminAuditBackfill__Enabled=false                                   # DISARM once parity is proven
```

Exit criterion is parity, not a green log: local `SELECT count(*) FROM admin_actions` must equal the row count of
`GET /v1/audit-events?application=jeeb-gateway`. Read an empty upstream page as evidence of nothing only after a
known-positive control shows that query returning rows.

**Superseded by this rule (G-21):** the design-flow DSN backfill variants — `delivery-service cmd/backfill-requests`
(reading gateway `delivery_requests` over an owner-supplied DSN) and `cmd/backfill-availability` (reading
`jeeber_availability` the same way). Both are **rewritten** to the shape above: service-token gateway export → idempotent
import on the target. If you find a DSN-reading backfill in a design doc, the doc is stale; this rule wins.

## 3. `CourierPositionQueue` disposition (PRE-08 — PLAN §3-C / §3-D)

`CourierPositionQueue` is **retained as-is** — it is a *deliberate retain*, not an oversight and not an extraction target:

- It is a **transient in-proc back-pressure buffer** between `POST /location/update` returning 200 and the realtime
  publish. It holds **no authoritative state**.
- **Restart-drop is benign**: a fix lost on restart or on bounded-channel overflow is one map position the customer does
  not receive, and the next GPS tick (≈1s later) supersedes it. The authoritative write (`ILocationStore`) happens first
  and synchronously.
- It is therefore **not a durable store** and **must not be promoted to the `StoreDurabilityGuard` critical roster**, and
  it is not counted as gateway-owned state that W5 has to remove. (`StoreDurabilityGuard` itself was deleted at
  W5-11, so the roster no longer exists; the disposition — transient, restart-drop benign, not an extraction
  target — is what still binds, and `CourierPositionQueue.cs:43-44` still cites this section for it.)

Do not confuse it with `NewRequestFanoutQueue` (made durable against state-service work-items in W1) or with
`InMemoryLocationStore` (retired in-program to geolocation-service). Those two are in scope; this one is not.

## 4. How a program PR is reviewed

A `gwdbx(...)` PR is read against the plan clause and guardrail IDs named in its commit body, plus the guard
scripts — which are run **by hand**. Every workflow in this repository is disabled at the GitHub level, so no
gate here is enforced automatically and nothing in this section may be read as a CI promise:

- `scripts/check-stateless-gateway.sh` — the R9 no-DB/no-volatile-store gate. Its seam allowlist
  (`scripts/gateway-db-seam-allowlist.txt`) is now **empty**, so the ratchet is absolute: a line added back is a
  GR-3 violation, not a rollback. The script also pins `AddHostedService` at 2 while the source registers 19, so
  it reports FAIL on main **by design** — that gap is the remaining stateless work, not a flake.
- **G-08 guard-roster manifest gate** — RETIRED. Its source of truth,
  `src/JeebGateway/Infrastructure/StoreDurabilityGuard.cs`, was deleted at W5-11, so
  `scripts/check-guard-roster.sh` exits non-zero before it reads anything and cannot be regenerated.
- **G-22 flag-containment gate** (`scripts/check-gwdbx-flag-registry.sh`) — merged and still meaningful when run
  by hand: repo flag keys must be a subset of the approved registry.

Reviewer checklist: smallest diff that satisfies the item; no runtime-behaviour change unless the item says so;
no flag key outside the registry; and none of §1 violated. (The old "StoreDurabilityGuard roster edited in the
same PR as any store deletion" clause is void — there is no roster to edit.)

### 4.1 G-22 inventory scope (owner decision D1 — ruled, option A; scope bugs closed by D2)

`scripts/check-gwdbx-flag-registry.sh` inventories the repo through four arms, and every token it produces must appear
in `scripts/gwdbx-flag-registry.txt`:

1. **`CONFIG_FLAGS`** — **every** jq-flattened `appsettings*.json` key (array indices collapse to the element
   property, `FeatureFlags:` prefix stripped). It does **not** filter on how the key is spelled.
2. **`CODE_FLAGS`** — **every** options class bound anywhere under `src/JeebGateway` via `Configure<T>(…GetSection(…))`,
   `AddOptions<T>()…Bind(…)` or `GetSection(…).Get<T>()`, contributing its bindable public properties as
   `<SectionName>:<PropertyName>` (section from the class's `SectionName` const, recursing into nested option types,
   reading **every** file that declares the class). Before D1 this arm read `Services/UpstreamFeatureFlags.cs` only.
3. **`READ_FLAGS`** — literal keys read straight off `IConfiguration` with no options class in between:
   `GetValue<T>("k")`, `GetSection("k")`, `config["k"]`, and `const string …Key = "k"`.
4. **`MODE_TOKENS`** — `*Mode` identifiers under `src/`, minus `FRAMEWORK_MODE_TYPES`.

All four arms read untracked files too, because a brand-new options file is exactly how a flag arrives.

**What D1 fixed.** `FRAMEWORK_MODE_TYPES` is for framework *type* names, but it also held two real configuration
switches, so the registry was blind to both:

- **`SuperLogin:OpenMode`** (bound at the `SuperLoginOptions` site in `Program.cs`) — security-critical: when true the
  gateway mints a session token for an arbitrary `userId` with no service key and serves the demo-user roster
  anonymously. It appears in **no** `appsettings*.json` file; it is set only as the environment variable
  `SuperLogin__OpenMode` on the live server, so `CONFIG_FLAGS` could never see it and the `*Mode` arm was the only arm
  that could.
- **`WalletGuard:FailMode`** (bound at the `WalletGuardOptions` site) — selects fail-open vs fail-closed when
  wallet-service is down.

Both are now **registered rather than ignored**, under a fourth status **`setting`**: bound configuration that is not a
cutover flag, so it always carries owning-wave `-` and never a `create=`/`delete=` pair. Each appears twice, once as the
bare `*Mode`-arm token and once as the full key, because those are the two forms the arms actually emit.

`FRAMEWORK_MODE_TYPES` is now only `BoundedChannelFullMode`, `FullMode` (both `System.Threading.Channels`),
`SameSiteMode` (ASP.NET cookie enum) and `PushDeliveryMode` (a local enum in `Notifications/PushSilencePolicy.cs`, never
bound to configuration). **Re-verify against the binding sites before adding a fifth** — an entry here is a hole in the
registry.

**What D2 fixed.** D1 claimed the widened `CODE_FLAGS` meant an environment-only switch could never hide again. That was
false: the arm parsed `Configure<T>(…GetSection(…))` in `Program.cs` only, so the six classes bound through
`AddOptions<T>().Bind(…)` — `ServiceAuthOptions`, `DownstreamServicesOptions`, `PartnerWalletOptions`,
`PartnerAuthOptions` (in `Extensions/*.cs`), `NewRequestFanoutOptions` and `TrackingOptions` (in `Program.cs`) — were
inventoried by no arm at all, and neither was anything bound from an extension method. `ServiceAuth:Enabled`,
`PartnerWallet:MaxTransferAmount` and `StoreDurability:FailClosedDisabled` were live, switchable and unregistered. Four
scope bugs are closed, each with a probe that now fails the gate:

- the code arm scans **every** `.cs` file and all three binding idioms, not `Configure<T>` in `Program.cs`;
- bound types resolve to **every** file declaring that simple name, so a same-named class in a second namespace (or the
  other half of a `partial`) is no longer silently replaced by the alphabetically-first file;
- the config arm keeps **every** committed key — the old `FeatureFlags:|Migration:|…Mode` filter was a name-spelling
  guess that a `OpsBypass:SkipAuthentication` key walks straight past, and it is only luck that `SuperLogin:OpenMode`
  was spelled with a capital `M`;
- invariant (3) tests the forbidden names against the read arm as well, since `GetValue<bool>("FeatureFlags:UseUpstream:Payments")`
  revives a retired flag just as effectively as an options property does.

**What D3 fixed (G-05-PAYMENTS-KEY-RESIDUE).** All four arms above read the **repo's** spelling (`A:B`). The deploy
workflows set configuration in the **env** spelling (`--env-add FeatureFlags__UseUpstream__Payments='true'`,
`add_env FeatureFlags__UseUpstream__Ratings true`), and no arm saw a single one of them — `deploy-to-jeeb.yml` alone
carries 32 such literals. A contributor could therefore re-arm retired UPG routing by editing a workflow and every
gate would stay green. The **deploy-env arm** now scans the non-comment lines of `.github/workflows/*.yml`, normalises
`FeatureFlags__A__B` → `A:B`, and feeds invariants (2) and (3). Measured at 12 tokens on introduction, all 12 already
registered, 0 forbidden. Full-line `#` comments are dropped, so the workflows' own tombstone and how-to blocks stay
allowed mentions. Three probes: an active `--env-add …__Payments='true'` is rejected by (3); the same name in a `#`
comment passes; an unregistered `FeatureFlags__OpsBypass__SkipEverything` is rejected by (2).

Note for readers today: `.github/workflows/**` is off-limits — nothing under it may be added, edited or
re-enabled — so the deploy-env arm now guards an edit path that policy has already closed, and it only guards it
when someone runs the script.

**Known limit.** A key assembled at runtime (`config[$"{Section}:Enabled"]`, `config[someVariable]`) is not a literal and
no static arm can see it. Write configuration keys as literals or as a `const string …Key`; a computed key is a review
finding, not a gate finding.
