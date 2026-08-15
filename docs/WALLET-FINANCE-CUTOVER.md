# Wallet finance cutover

**Status:** COMPLETE on the gateway side (W5-10 / W5-11). Sections marked HISTORY below
describe machinery that no longer exists; they are kept as the record of how the cutover
ran, not as procedure. Reviewed against `main` on 2026-08-16.

Wallet-service is the only balance and transaction-ledger owner. The gateway owns Jeeb fee policy
and translates a COD settlement into one generic wallet transaction header:

1. configured system USD wallet → Jeeber USD wallet for the gross COD amount;
2. Jeeber USD wallet → configured system USD wallet for commission;
3. Jeeber USD wallet → configured system USD wallet for insurance, when non-zero.

Wallet-service executes all legs atomically. The gateway sends
`Idempotency-Key: settlement:<gateway-settlement-id>` and never mutates a balance or wallet table.
There is no service-auth header; the wallet endpoint is reachable only on the private network.

The shared gateway opaque-role append seam blocks a `driver` grant unless an idempotent
`PUT Wallet/holder/ensure` first converges the same UUID holder to exactly one active wallet in
every currency returned by `GET Fees/currencies`. The call carries no user bearer or service-auth
header and stays inside the private overlay. Wallet-service does not interpret the role; the Jeeb
role decision remains entirely in the gateway/User Management boundary.

## Phase A (HISTORY — completed, no longer operable)

Phase A ran `WalletLedgerMigration:ShadowCompareEnabled` as a read-only comparison of
holder-ledger API reads against a legacy WalletPostgres projection, and
`SettlementShadowCompareEnabled` against a `settlement_ledger_entries` row.

None of that machinery survives. The WalletPostgres projection and its DSN were deleted at
W5-10; the four settlement tables (`settlements`, `settlement_enqueue`,
`settlement_ledger_entries`, `settlement_batches`) were dropped under owner ruling A23; and
`SettlementLedgerReconciler` no longer exists in `src/`. `ShadowCompareEnabled` survives as an
inert property on `WalletLedgerMigrationOptions` and is still committed `true` in
`appsettings.json` and `appsettings.Production.json`, where it now selects nothing.

What replaced it: `WalletLedgerMigration:Authority` is committed `wallet-api` in
`appsettings.json` and `appsettings.Production.json`. Any other value resolves
`NullJeebWalletLedgerReader` and serves an empty ledger page — there is no Postgres to fall
back to. `appsettings.Development.json` deliberately keeps `postgres`, which in dev means that
empty page.

## Reconciliation/backfill tool (HISTORY — one-time, superseded by G-21)

> **Do not run the commands in this section.** `tools/WalletFinanceBackfill` is still in the
> tree, but the invocations below read another service's database over `--gateway-dsn-env` /
> `--delivery-dsn-env`, which guardrail **G-21** forbids outright — and the gateway DSN they
> name no longer exists. Any future reconciliation must be a service-token export from the
> owning service plus an idempotent import on the target. The description is retained because
> it is the record of what the one-time run actually did.

The one-time tool derived the complete active Jeeber population only from User Management users
whose `AvailableRoles` contained the opaque `driver` role. It then read gateway financial rows and
delivery-service's non-financial completion markers for reconciliation — the cross-service DSN
shape G-21 now forbids. A settlement whose Jeeber was absent from the User Management population
was flagged and never created a holder or posted a wallet transaction. Delivery-only rows were
reported rather than used to invent missing amounts.

Connection strings were supplied indirectly through environment-variable names and never
printed. Dry-run was the default and performed zero wallet PUT/POST requests:

```sh
dotnet run --project tools/WalletFinanceBackfill -- \
  --user-management-dsn-env JEEB_USER_MANAGEMENT_DSN \
  --gateway-dsn-env JEEB_GATEWAY_DSN \
  --delivery-dsn-env JEEB_DELIVERY_DSN \
  --wallet-base-url http://wallet-service:8080/ \
  --require-clean
```

After an owner reviewed the JSONL artifact, execution required a second explicit confirmation:

```sh
dotnet run --project tools/WalletFinanceBackfill -- \
  --user-management-dsn-env JEEB_USER_MANAGEMENT_DSN \
  --gateway-dsn-env JEEB_GATEWAY_DSN \
  --delivery-dsn-env JEEB_DELIVERY_DSN \
  --wallet-base-url http://wallet-service:8080/ \
  --execute --confirm wallet-authoritative-backfill --require-clean
```

The run was restart-safe: every active User Management Jeeber was inspected before any settlement,
holder provisioning was an idempotent `PUT`, transaction initiation reused the same durable key,
and execution was idempotent on the returned header id.

## Phase B gate (SATISFIED)

The criteria below were the gate for removing the gateway finance projection and reconciler.
They are kept as the historical record of what had to be true; the removal has since happened
(W2-R02 dropped the settlement tables under A23, W5-10 deleted the WalletPostgres seam,
W5-11 deleted the reconciler). Nothing in this list is an outstanding action.

- zero source/identity mismatches (or an owner-approved exception list);
- every configured-currency wallet is present exactly once per active User Management Jeeber;
- every eligible gateway settlement replays to one executed wallet header;
- gross, commission, insurance, currency, holder and external delivery reference match per row;
- no unresolved wallet-post failures and no rising reconciliation backlog.

`GatewayPostgres` is no longer a gateway seam and no gateway store is Postgres-backed;
wallet-service is the sole owner of balances and the transaction ledger, and
settlement-service owns the money rows the gateway used to hold.
