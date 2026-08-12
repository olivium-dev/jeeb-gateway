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
