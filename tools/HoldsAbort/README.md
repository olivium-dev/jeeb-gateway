# HoldsAbort — break-glass release of every outstanding offer hold. Owner-run.

Risk **R4**, DECISION Operation 5. The gateway places a wallet-service **pending** transaction per
live offer as the reservation. **wallet-service never expires a pending header** — there is no TTL,
no reaper, no expiry column (`exec/_evidence/w0/RESEARCH-holds.md` §1). A pending header holds funds
forever until an explicit `execute` or `abort`, so *the gateway owns release*. The in-process
`HoldSweeper` does that continuously; **this tool is the manual total-release when the sweeper
cannot** — it is switched off, wedged, mid-rollback, or the holds themselves must all go away.

Operational runbook (prerequisites, the census cross-check, verification, rollback):
**`_wallet-guard-fix/exec/RUNBOOK-holds-abort.md`**. Read that first — this file is the tool's
reference, not the procedure.

- **Dry-run is the default.** Without `--execute` there are ZERO writes: no abort, no tombstone.
  The run is one state-service prefix scan plus one `GET Transaction/by-external-reference/...` per
  record, plus the holder balance reads, and it prints the full plan it *would* execute.
- **Wallet + state HTTP APIs only.** The tool never opens a database and contains no SQL. The
  read-only census SELECT in the runbook is a separate, owner-run `psql` cross-check.
- **Money-neutral.** Abort releases a hold; it moves no money and can never clobber an executed
  transaction. This tool never calls `Transaction/{id}/execute`.
- **Idempotent.** Abort is idempotent upstream, and a record whose holds are already gone is simply
  tombstoned. Re-running after a partial run is safe and expected.
- **`already executed` is never retried.** wallet-service answers abort-after-execute with a 500
  ("Transaction already executed"). That is terminal — the money moved. The tool **skips and
  reports** it and leaves the record alone; a human reconciles that offer.

## Rules — read before running

- **Execution is OWNER-GATED.** No agent runs this, in either mode, against any server.
- **MSI is READ-ONLY for this programme.** Do not point this tool (not even a dry run) at the live
  MSI stack without the owner's explicit word for that specific run.
- No manual server changes: the tool is committed code and the JSON report it writes is the
  evidence artifact.

## Usage

```
--wallet-url <URL>            default http://127.0.0.1:10014   (env WALLET_SERVICE_URL)
--state-url <URL>             default http://127.0.0.1:10073   (env JEEB_STATE_SERVICE_URL)
--wallet-token <TOKEN>        env WALLET_SERVICE_TOKEN         (wallet-service has no auth today)
--state-token <TOKEN>         env JEEB_STATE_SERVICE_TOKEN
--state-token-file <PATH>     env JEEB_STATE_SERVICE_TOKEN_FILE (the gateway's mounted secret)
--jeeber <ID>                 partial mode: only this jeeber's holds
--dry-run                     DEFAULT
--execute                     abort + verify + tombstone
```

Exit codes: `0` clean · `1` errors · `2` usage.

## What one record costs

Per `wgf:hold:<offerId>` intent record:

1. `GET /Transaction/by-external-reference/jeeb:offer:<offerId>` — the full hold set (base + raise
   deltas share one external reference). Headers with `status: -1` are PENDING; `0` executed,
   `-2` aborted.
2. `--execute` only: `POST /Transaction/{txId}/abort` for each pending header.
3. `--execute` only: re-read the external reference. If **anything** is still pending, or any abort
   failed, the record is **not** tombstoned — it is the sweeper's only handle on those holds
   (DECISION I2), and throwing it away would strand them.
4. `--execute` only: `PUT /v1/state/idempotency` writes the record back with `state:"closed"` on a
   60-second TTL. The KV has **no DELETE**, so a tombstone *is* the delete; the sweeper and this
   tool both read `closed` as absent.

Records already carrying `state:"closed"` are counted and skipped. `--jeeber` filters on the intent
record's `jeeberId` (GUID-normalised); everything else is reported as `filtered_out`.

## Verification: netted == gross

`GET /Wallet/holder/{id}/wallets` returns the **netted** balance — wallet-service subtracts pending
outgoing legs from it (S-10). So once every pending leg is aborted, the netted read equals the
settled (gross) balance. The tool snapshots each touched holder's wallets before and after and
asserts the rise equals exactly what it released. A mismatch is an error, and it means either
another writer moved that holder's money during the run or a hold is still pending — never
"probably fine".

## Report

Every run writes `holdsabort-<mode>-<yyyyMMdd-HHmmss>.json` to the working directory plus a plan on
stdout and a summary line on stderr:

```
holds-abort mode=dry-run scanned=14 actionable=11 released=0 errors=0 report=…
```

`plans[].outcome` is the per-record verdict: `would-abort-<n>-pending-then-tombstone`,
`would-tombstone-stale-record`, `released`, `stale-record-tombstoned`,
`incomplete-<n>-still-pending`, `incomplete-blocked`, `read-failed`, `verify-failed`. Diff a dry run
against the `--execute` run that follows it: the actionable set must be the same.

## Rollback

There is nothing to roll back — an aborted hold is released funds, and the tombstone only retires a
bookkeeping record. To stop the gateway placing *new* holds, set `Holds:Enabled=false` (see the
runbook §"Rollback"): admission falls back to the aggregate exposure check and no code change is
needed. If holds are re-enabled later, the sweeper's MISSING branch re-places a hold for every live
offer, so a release performed here is not permanent.
