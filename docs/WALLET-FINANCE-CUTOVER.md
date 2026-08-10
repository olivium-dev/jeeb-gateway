# Wallet finance cutover

Wallet-service is the only balance and transaction-ledger owner. The gateway owns Jeeb fee policy
and translates a COD settlement into one generic wallet transaction header:

1. configured system USD wallet → Jeeber USD wallet for the gross COD amount;
2. Jeeber USD wallet → configured system USD wallet for commission;
3. Jeeber USD wallet → configured system USD wallet for insurance, when non-zero.

Wallet-service executes all legs atomically. The gateway sends
`Idempotency-Key: settlement:<gateway-settlement-id>` and never mutates a balance or wallet table.
There is no service-auth header; the wallet endpoint is reachable only on the private network.

## Phase A: authoritative API plus read-only comparison

`WalletLedgerMigration:ShadowCompareEnabled` compares holder-ledger API reads against the legacy
WalletPostgres projection. `WalletLedgerMigration:SettlementShadowCompareEnabled` compares a
successful wallet settlement request against an existing `settlement_ledger_entries` row. Both
flags default to `false`; shadow failures and mismatches are logged and can never replace the wallet
response. Enabling either flag requires its legacy DSN. Neither comparator has a write method.

The temporary gateway `settlements` row remains a receipt/outbox projection in this phase. If a
wallet response is lost, `SettlementLedgerReconciler` replays the same wallet initiation and
execution idempotently and stamps the returned wallet transaction-header id.

## Reconciliation/backfill tool

The shared gateway opaque-role append seam now blocks a `driver` grant unless an idempotent
`PUT Wallet/holder/ensure` first converges the same UUID holder to exactly one active wallet in
every currency returned by `GET Fees/currencies`. The call carries no user bearer or service-auth
header and stays inside the private overlay. Wallet-service does not interpret the role; the Jeeb
role decision remains entirely in the gateway/User Management boundary.

The one-time tool derives the complete active Jeeber population only from User Management users
whose `AvailableRoles` contains the opaque `driver` role. It then reads gateway financial rows and
delivery-service's non-financial completion markers for reconciliation. A settlement whose Jeeber
is absent from the User Management population is flagged and never creates a holder or posts a
wallet transaction. Delivery-only rows are reported rather than used to invent missing amounts.

Connection strings must be supplied indirectly through environment-variable names and are never
printed. Dry-run is the default and performs zero wallet PUT/POST requests:

```sh
dotnet run --project tools/WalletFinanceBackfill -- \
  --user-management-dsn-env JEEB_USER_MANAGEMENT_DSN \
  --gateway-dsn-env JEEB_GATEWAY_DSN \
  --delivery-dsn-env JEEB_DELIVERY_DSN \
  --wallet-base-url http://wallet-service:8080/ \
  --require-clean
```

After an owner reviews the JSONL artifact, execution requires a second explicit confirmation:

```sh
dotnet run --project tools/WalletFinanceBackfill -- \
  --user-management-dsn-env JEEB_USER_MANAGEMENT_DSN \
  --gateway-dsn-env JEEB_GATEWAY_DSN \
  --delivery-dsn-env JEEB_DELIVERY_DSN \
  --wallet-base-url http://wallet-service:8080/ \
  --execute --confirm wallet-authoritative-backfill --require-clean
```

The run is restart-safe: every active User Management Jeeber is inspected before any settlement,
holder provisioning is an idempotent `PUT`, transaction initiation reuses the same durable key,
and execution is idempotent on the returned header id.

## Phase B gate

Do not remove the legacy tables. Disable both shadow flags and remove the gateway finance
projection/reconciler registrations only after an MSI observation window establishes all of the
following:

- zero source/identity mismatches (or an owner-approved exception list);
- every configured-currency wallet is present exactly once per active User Management Jeeber;
- every eligible gateway settlement replays to one executed wallet header;
- gross, commission, insurance, currency, holder and external delivery reference match per row;
- no unresolved wallet-post failures and no rising reconciliation backlog.

GatewayPostgres remains available for unrelated gateway-owned stores throughout the cutover.
