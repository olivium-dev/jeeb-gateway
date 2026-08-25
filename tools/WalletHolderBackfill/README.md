# WalletHolderBackfill — one spendable wallet per currency, per user. Owner-run.

OD-C2-1 note / RULINGS item 11. Until W2 the gateway provisioned wallets on **one** seam only —
the jeeber (`driver`) role grant. Signup and role-switch were passthroughs, and any grant written
straight into user-management bypassed the decorator entirely. W2 closes the seam going forward
(signup + role-switch now ensure best-effort); **this tool converges everyone who already exists**.

A user is **INCOMPLETE** when any configured wallet currency has no active *spendable*
(non-`cod_*`) wallet. The holds mechanism is NATIVE pending-hold, so there is no `jeeb_hold`
wallet to look for — "all required wallets" is exactly one active spendable wallet per currency,
which is what `WalletServiceJeeberWalletProvisioner.EnsureAsync` already converges to.

- **Dry-run is the default.** Without `--apply` there are ZERO wallet writes — not even
  `holder/ensure`. The run is `GET Fees/currencies` once plus one `GET Wallet/holder/{id}/wallets`
  per user.
- **Wallet writes over the HTTP API only.** The tool reuses the gateway's own provisioner verbatim
  (idempotent `PUT Wallet/holder/ensure`); the wallet-service repo and its database are never
  touched directly.
- **Only enumerated users are ever ensured.** wallet-service :10014 has **no auth** and will mint a
  holder for any GUID handed to it (W0 census §3). Ids come from the user-management `Users` table
  (or a file derived from it) and from nowhere else.
- **Idempotent.** Ensure is a PUT that preserves existing wallet types and creates only what is
  missing, so a second run reports every user `complete` and creates nothing.

## Rules — read before running

- **Execution is OWNER-GATED.** No agent runs this, in either mode, against any server.
- **MSI is READ-ONLY for this programme.** Do NOT run this tool (not even a dry run) against the
  live MSI stack without the owner's explicit word for that specific run.
- No manual server changes: the tool is committed code, and the census file it writes is the
  evidence artifact.

## Usage

```
--wallet-url <URL>                 default http://127.0.0.1:10014
--users-file <PATH>                one GUID per line ('#' comments and blank lines skipped)
--um-connection <NPGSQL_CONN>      read-only: SELECT "Id" FROM "Users"
--dry-run                          DEFAULT
--apply                            perform holder/ensure for incomplete users
```

Exactly one of `--users-file` / `--um-connection` is required.
Exit codes: `0` clean · `1` errors · `2` usage.

Every run writes `walletholder-census-<yyyyMMdd>.json` to the working directory and one summary
line to stderr:

```json
{"generated_at":"…","mode":"dry-run","wallet_url":"http://127.0.0.1:10014",
 "currency_ids":[1,2],"users_scanned":27,
 "complete":["…"],
 "incomplete":[{"user_id":"5ae06873-…","holder_exists":true,"missing_currency_ids":[2]}],
 "created":[],"errors":[]}
```

`incomplete` is what the scan *found*; `created` is what `--apply` ensured. In a dry run `created`
is always empty, which is how the two runs diff.

## Runbook (owner)

Wallet-service is not exposed off-host, so this runs over loopback on the box that owns it:

```bash
cd tools/WalletHolderBackfill

# 1. DRY RUN FIRST. Always. Reads only.
dotnet run -- --um-connection "Host=127.0.0.1;Port=5442;Database=jeeb-user-management;Username=…;Password=…"

# 2. Read walletholder-census-<date>.json. Proceed only if errors == 0 and the
#    incomplete list matches the expectation below.

# 3. OWNER-GATED write.
dotnet run -- --um-connection "…" --apply

# 4. Re-run the dry run: incomplete must now be empty.
dotnet run -- --um-connection "…"
```

If the DB connection string is not available, feed the same ids through `--users-file` instead —
the file must be derived from the `Users` table, never hand-assembled from wallet ids.

### Expected live diff (W0 census, 2026-08-24)

27 users; currencies `[1 Credit, 2 USD]`; USD(2) is the fee currency. **22 of 27 users lack a
spendable USD wallet** — including 2 of the 7 `driver`-role users
(`5ae06873-25b6-466d-bc09-69b402570e7d`, `fedb6e3b-0ab3-4e49-886f-d2047bb6f92a`), which is the
decorator-bypass evidence. So a first dry run should read roughly:

```
walletholder-backfill mode=dry-run users=27 complete=5 incomplete=22 created=0 errors=0 census=…
```

and `--apply` should report `created=22`, after which every subsequent run is `incomplete=0`.
Materially different counts mean the census is stale — re-take it before applying.

## Rollback

**None is needed.** Ensure creates zero-balance wallets and, where absent, an active holder;
nothing is deleted, no balance is written, and no existing wallet type is changed. A created
wallet is inert until a real transaction touches it. If a wallet must be undone anyway, deactivate
it via `POST Wallet/{holderId}/{walletId}/deactivate` (wallet-service enforces the zero-balance
invariant server-side) — but note the gateway will simply re-ensure it at the next signup,
role-switch or jeeber grant for that user.
