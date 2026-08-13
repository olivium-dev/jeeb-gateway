# Gateway DB Extraction — Program Rules (gwdbx)

**Service**: jeeb-gateway · **Program**: gateway-db-extraction-2026-08-12
**Plan of record**: `jeeb/PLAN-gateway-db-extraction-2026-08-12.md` (v2, binding)
**Playbook**: `jeeb/PLAYBOOK-gateway-db-extraction-2026-08-12.md` · guardrails/checklists: `jeeb/_artifacts/gw-db-extraction-20260812/`

The extraction of `GatewayPostgres` (37 tables / 20 domains) into the existing service fleet is **in flight**: waves W0–W5
move each domain to its owning service and W5 deletes both connection strings, the migration folder and the DB health
checks. Until that finishes, the gateway still owns tables — so the rules below apply to **every** PR touching this repo,
not only to program PRs. Nothing here is new policy: each clause is traceable to the plan or the playbook.

## 1. Never-do list (PRE-06)

- **Never merge `codex/ops-gateway-residue-20260810-wip`.** That branch is frozen and broken; the only sanctioned way to
  take anything from it is a **cherry-pick of `6d46e69`** (the wallet-ledger lineage). — G-05
- **Never revert gateway PR #385.** It carries the `6d46e69` lineage live at `66a7b9d`
  (`WalletServiceJeebWalletLedgerReader` + `WalletLedgerMigration` options); the W0 ledger flip is built on it. — G-05
- **UPG is retired — never re-add `UseUpstream:Payments`** (or a `"Payments"` upstream key). The key was deleted
  2026-07-27; the surviving mentions are tombstone comments/docs. Dispute-refund keeps returning 400/no-op. — G-05, PLAN §8
  The env spelling `FeatureFlags__UseUpstream__Payments` outlived the repo deletion in the live `gateway.env`
  (value `false`, zero read sites, so inert) until 2026-08-14; deleted there, and the flag-registry gate's D3
  deploy-env arm now fails CI on the env spelling too. — G-05-PAYMENTS-KEY-RESIDUE
- **notification-service is never a target of this program (R7).** No PRs, no jeeb vocabulary there. The gateway calls only
  its existing generic surfaces (`ServiceNotificationClient`, `JeebNotificationRecordClient`); the outbox goes to
  state-service work-items + push-notification instead. — G-25
- **No new repos and no new deployables** — every domain lands on an already-existing fleet service. — G-26

## 2. Backfill standard (PRE-07 — rider R2, guardrail G-21)

Every backfill in this program has the same shape. No exceptions, no "just this once":

1. It consumes an **idempotent HTTP ingest/import endpoint in the OWNING service** — or it is a gateway-resident replay
   job that POSTs to that target's ingest route. The code lives in the owning service or in the gateway; never in a
   standalone tool that talks to someone else's database.
2. **No cross-service DSN reads — ever.** A backfill must not add a `ConnectionString`/DSN pointing at another service's
   database: that recreates exactly the coupling this program deletes.
3. It is **dryRun-capable and re-runnable** end to end — a double run is a no-op.
4. The **owner runs it**. Agents never run backfills, never deploy, and never touch `192.168.2.20`. — PLAN §6 (O2), §8

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
  it is not counted as gateway-owned state that W5 has to remove.

Do not confuse it with `NewRequestFanoutQueue` (made durable against state-service work-items in W1) or with
`InMemoryLocationStore` (retired in-program to geolocation-service). Those two are in scope; this one is not.

## 4. How a program PR is reviewed

A `gwdbx(...)` PR is read against the plan clause and guardrail IDs named in its commit body, plus the CI gates:

- `scripts/check-stateless-gateway.sh` — the existing R9 no-DB/no-volatile-store gate. Its seam allowlist
  (`scripts/gateway-db-seam-allowlist.txt`) may only **shrink**: each store elimination deletes its line in the same PR.
- **G-08 guard-roster manifest gate** and **G-22 flag-containment gate** — the two W0 gates added by sibling program PRs
  (roster orphans fail the build; repo flag keys must be a subset of the approved registry). Named here for reference
  only — do not assume either is merged yet; check the workflow before relying on it.

Reviewer checklist: smallest diff that satisfies the item; no runtime-behaviour change unless the item says so; the
StoreDurabilityGuard roster edited in the **same PR** as any store deletion; no flag key outside the registry; and none of
§1 violated.

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
allowed mentions. Three probes: an active `--env-add …__Payments='true'` fails (3); the same name in a `#` comment
passes; an unregistered `FeatureFlags__OpsBypass__SkipEverything` fails (2).

**Known limit.** A key assembled at runtime (`config[$"{Section}:Enabled"]`, `config[someVariable]`) is not a literal and
no static arm can see it. Write configuration keys as literals or as a `const string …Key`; a computed key is a review
finding, not a gate finding.
