# ADR-0011 — O1: collecting the platform commission

Status: accepted (O1, owner ruling 2026-08-16) · Supersedes nothing · Raised from OA-30

## Context

The commission was **booked and never collected, by anyone, ever**. settlement-service writes
`state=settled, commission=…` in one local transaction with zero outbound HTTP; the wallet ledger
held **0 fee entries across 275 parties** against **81 `Done` deliveries**. No deployed code path
debited a fee. Its `POST /settlements/{id}/external-ref` hook existed with no producer.

The owner ruled:

> "Jeeber charge certain amount lets say 113.7 usd, it is completly random, jeeber can charge as much
> has he wants. Then each time he offer the system should check if he has enough in his wallet to pay
> the fees, this specific wallet should never be used to handle cash on delivery."

Three rungs. **Two of the three were already true in code** and are now pinned by tests rather than
rebuilt:

| Rung | Where it already lives | Status |
|---|---|---|
| 1 — free-form price, no cap | `RequestOffersController.MinimumFee = 1m`; no maximum anywhere | already true |
| 2 — offer-time wallet check | `WalletSufficiencyGuard`, at submit / accept / edit; 402 on short, 503 on outage | already true |
| 3 — the fee wallet never handles COD | `SpendableWalletTypes` denies `cod_*` on the read side | already true on read; **now also pinned on the write side** |

What was missing is the fourth thing the ruling presupposes: the fee is actually **taken**.

## Decision

**The gateway is the debiting orchestrator.** It is the only component holding both the money facts
and a wallet client, and neither neighbour may do it:

- settlement-service is contractually forbidden from calling anyone (A6/A21; its README and a
  zero-`HttpClient` audit both hold).
- wallet-service must stay generic (Decisions 1–4 of `ouday-wallet-decison.md`, the G-28 gate). Its
  sanctioned fit for jeeb is balances, ledger reads, the sufficiency guard and **idempotent
  transaction primitives** — exactly what is used here, and nothing more. **No wallet-service change
  was made.**

Shape: on the settle that freshly books a commission, `WalletCommissionCollector` resolves the
jeeber's fee wallet and the platform `__SYSTEM__` wallet, runs wallet-service's own
`Transaction/initiate` → `execute` saga, and stamps the transaction id back onto the settlement via
`POST /settlements/{id}/external-ref`.

### No state on the gateway

Exactly-once is delegated, not stored:

- **wallet-service** dedupes on `Idempotency-Key` (`settlement:{settlementId}`) + a server-computed
  request fingerprint, backed by a unique index. A replay returns the original transaction; the same
  key with a different body is a 409, never a silent second charge.
- **settlement-service** `external_ref` is first-stamp-wins in SQL and is the durable "already
  collected" marker, read back as `Settlement.WalletTxId`.

The gateway persists nothing. A re-drive of a lost or ambiguous collection is safe by construction.

### Failure posture

A settled delivery **never unwinds**. The customer already paid cash and the handover already
happened, so an uncollectable fee is a debt, not a reason to fail the delivery.

| Failure | Behaviour |
|---|---|
| Initiate rejected | nothing committed; no abort, no stamp; `settlement.commission.failures` |
| Execute 4xx (insufficient balance) | abort the pending hold (wallet-service never expires one), leave the row settled and unstamped; `settlement.commission.insufficient` |
| Execute 5xx / timeout | **deliberately not aborted** and not stamped — aborting a possibly-committed move is the double-move bug; `settlement.commission.uncertain` |
| Stamp fails after a successful debit | money stays moved, logged and counted; `settlement.commission.stamp_failures` |

## Two things the ruling did not settle

### 1. When is the fee debited? — **at completion (settlement). My call, not the owner's.**

The ruling says *check* at offer time. A check is not a debit, and the owner did not pick
reserve-at-offer (it was on the menu and left unselected). Completion wins on three grounds:

- **Terminal.** `Done` does not un-happen, so no reversal machinery is needed. Accept-time would
  need refunds for the ~47% of deliveries that cancel (188 of 397 live rows are `Cancelled`), and no
  reversal primitive is ratified — wallet-service rejects zero/negative legs outright.
- **Uniquely keyed.** The settlement id is a durable, system-generated id to key idempotency on.
  There is no equivalent at accept.
- **Already stamp-backable.** The `external-ref` hook exists and was built for this.

Reserve-at-offer was rejected on evidence, not preference: wallet-service has **no hold/authorization
API**, and its de-facto hold (a `Pending` transaction header) **never expires** — no TTL, no sweeper.
A reserve on every losing bid would strand funds permanently and block wallet deactivation, and the
expiry sweeper would have to live somewhere the gateway is not allowed to keep state.

**Honest consequence:** the offer-time check is therefore advisory. A jeeber can pass it, spend the
balance elsewhere, and be short at settlement. The accept-time guard narrows the window; it does not
close it. `settlement.commission.insufficient` is how that gap is measured rather than assumed away.

### 2. How is the fee computed? — **not my call; already an owner ruling.**

Owner ruling **Q-001 (2026-07-07)**: flat **10%** of the accepted offer, insurance retired, no
minimum-fee floor. It is implemented in `settlement-service/Domain/CommissionPolicy.cs`
(`FlatRate = 0.10m`, `MidpointRounding.AwayFromZero`) and mirrored by the gateway's
`CommissionCalculator.FlatRate` and `WalletGuardContract.RequiredCommission`. The one observed live
settlement, `commission = 0.3000`, is `Round(3.00 × 0.10)` — a $3.00 accepted fee. Nothing was
guessed.

The collector debits `Settlement.Total` **verbatim** and never recomputes it, so the booked fee and
the collected fee cannot drift. Note the rate is a compile-time `const` in **two** repos; changing it
is a code change in both, not a config flip.

## Off by default

`CommissionCollection:Enabled` ships **`false`**, with an explicit value and comment in
`appsettings.json` and `appsettings.Production.json`. Merging this must not start moving money, and a
later deploy must not start moving money either without an owner-gated flip.

This is deliberately not a repeat of `CodSettlementMode`, which was inert for months because it was
set in **no config file at all** and nothing counted the skips. Here the disabled path emits
`settlement.commission.skipped` on every settle, so "the fee is still not being collected" is a
number on the board rather than an absence nobody can see.

## What this does NOT do

- It does not backfill the 81 uncharged deliveries. 80 of them have no settlement row at all. That is
  owner-gated and untouched.
- It does not move any real money: nothing was deployed and the gate is off.
- It does not make the mobile offer-composer copy true. *"$X is reserved from your wallet now ·
  charged only if you win · released if you're not picked"* and `fundingReserveBody` describe
  reserve-at-offer, which this ADR rejects. `JeebWalletProjection.ReservedNow` stays hard-coded `0`
  because nothing reserves anything. **The copy is false and must change** — raised as an OA; mobile
  is a separate repo and a store release is out of scope here.
- It does not resolve OQ1 (the wallet-guard fail-open/fail-closed question), which remains the
  owner's.
