# WalletCurrencyMigration — currency-1 (Credit) → USD(2), owner-run

OD-C3-3 / RULINGS item 8. W1 pins the gateway's guard, debit and balance projection to the fee
currency USD(2). Any balance still sitting in currency 1 becomes invisible to the platform after
that pin — this tool moves it, and **only the owner runs it, at deploy time**.

- **Dry-run is the default.** Without `--execute --confirm currency-one-usd-migration` there are
  ZERO wallet writes: no `holder/ensure`, no `Transaction/initiate`, no deactivate.
- **Wallet HTTP API only.** The tool opens no database connection and holds no credentials; the
  only input it needs is the wallet-service base URL.
- **Idempotent by live re-read.** Every run re-reads balances. A holder whose live currency-1
  spendable balance is already `0` is skipped, so a second run is a no-op.

## What it does per run

1. `GET Fees/currencies` — asserts both census currencies exist and derives
   `rate = rate(source) / rate(target)` from the live catalog. The rate is never hardcoded.
2. `GET /system-wallet` — locates the `__SYSTEM__PRIMARY__` wallet in the target currency (the
   funding source) and the `__SYSTEM__` wallet in the source currency (the retirement sink), then
   cross-checks the sink against the census `systemCcy1WalletId`. A mismatch exits **3**.
3. `GET Wallet/holder/{id}/wallets` per census holder — sums the active, spendable
   (non-`cod_*`) currency-1 balance `X` and locates the holder's target-currency wallet.
4. `POST Transaction/validate` — probes the two-leg shape every run, before anything can move.
   For holders whose USD wallet does not exist yet the tool still reports a run-level
   `system_source_probe`, so the owner learns whether wallet-service accepts the system-funded
   leg *before* any wallet is minted.
5. Execute mode only: `PUT Wallet/holder/ensure` mints the missing USD wallet, then ONE
   `Transaction/initiate` + `Transaction/{id}/execute` per holder carrying both legs
   (w0 §2, value-preserving, both currencies stay net-zero):
   - **leg A (retire)** user currency-1 wallet → `__SYSTEM__` currency-1 wallet, `Amount = X`
   - **leg B (issue)** `__SYSTEM__PRIMARY__` USD wallet → user USD wallet, `Amount = rate * X`

   `ApplyConfiguredFees=false` (every leg is explicit), `Tag: ccy1-usd-migration`,
   `ServiceName: jeeb-gateway-tools`, `Idempotency-Key: ccy1-migration:<holderId>`.
   If validate rejects the batch, the documented per-holder 2-transaction fallback runs instead
   (issue first, then retire, keys `…:issue` / `…:retire`); if the issuance leg alone is also
   rejected, that holder errors out and nothing is moved for them.
6. `--deactivate-drained` (execute mode) — `POST Wallet/{holderId}/{walletId}/deactivate` for the
   emptied currency-1 wallets. wallet-service enforces the zero-balance invariant server-side.

Per-row JSON goes to **stdout**, the run summary JSON to **stderr**.
Exit codes: `0` clean · `1` errors · `2` usage · `3` census drift.

## Runbook

Run it **on the MSI box over loopback** — wallet-service is not exposed off-host:

```bash
cd tools/WalletCurrencyMigration

# 1. DRY RUN FIRST. Always. Reads only.
dotnet run -- --wallet-base-url http://127.0.0.1:10014

# 2. Read the stderr summary. Proceed only if census_drift == 0 and errors == 0.

# 3. OWNER-GATED execution.
dotnet run -- --wallet-base-url http://127.0.0.1:10014 \
  --execute --confirm currency-one-usd-migration --deactivate-drained

# 4. Re-run the dry run: every holder must now report skipped_already_migrated.
dotnet run -- --wallet-base-url http://127.0.0.1:10014
```

### Expected dry-run summary for the committed census

`census-2026-08-24.json` holds the W0 census: 5 test holders, 30.00 Credit total, 4 of whom have
no USD wallet yet. At catalog rate 0.1 that is 3.00 USD.

```json
{"mode":"dry-run","source_currency_id":1,"source_currency_code":"Credit",
 "target_currency_id":2,"target_currency_code":"USD","conversion_rate":0.1,
 "system_source_probe":"accepted","holders_scanned":5,"holders_skipped":0,"holders_ready":5,
 "holders_migrated":0,"wallets_ensured":0,"wallets_deactivated":0,"census_drift":0,"errors":0,
 "source_currency_delta":-30.00,"target_currency_delta":3.00}
```

Per holder: `10.00 → 1.00`, and `5.00 → 0.50` four times. Four rows report
`"target_wallet_state":"missing"` and `"validate_probe":"deferred_target_wallet_missing"`;
holder `42b7d440-…` already has a USD wallet, so its own two-leg probe runs.
After a successful execution the same command must report `holders_skipped: 5`,
`holders_ready: 0` and both deltas `0`.

**Anything else means STOP.** `census_drift > 0` (exit 3) says the live balance or the system
wallet id no longer matches the committed census: re-census (W0 method) and commit an updated
census file before executing. The tool never moves money for a drifted holder.

## Rollback

Nothing here is destructive — no wallet is deleted and no balance is overwritten; every movement
is an ordinary double-entry transaction. To undo an executed migration, post the **compensating
reverse transaction** (legs swapped: user USD → `__SYSTEM__PRIMARY__`, `__SYSTEM__` currency-1 →
user currency-1) under a **new** idempotency key; re-posting the original key only replays the
original transaction. If `--deactivate-drained` ran, re-mint the currency-1 wallet first with
`PUT Wallet/holder/ensure`. A run that dies midway is safe to simply re-run: the idempotency key
replays a transaction that was initiated but not executed, and execute is idempotent on the
transaction id.

## Rules

- Execution is **owner-gated at deploy time** (RULINGS item 8). No agent runs `--execute`.
- The gateway is correct with or without this migration: unmigrated holders simply see honest
  fee-currency zeroes until it runs.
- The wallet-service currency catalog is never edited — currency 1 stays a dormant catalog entry.
  This tool changes DATA only.
