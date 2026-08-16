# ADR-0011 — O1: collecting the platform commission

Status: accepted, **AMENDED 2026-08-16 by the owner** · Supersedes nothing · Raised from OA-30

> **AMENDMENT — the debit moved from completion to ACCEPT.** The owner reversed the implementer's
> timing ruling the same day:
> *"Once the user become a jeeber he should have a wallet, the wallet only drain when he make an
> offer and it is accepted"*.
> The original reasoning is kept below, struck through where it no longer governs, because the
> evidence behind it did not evaporate — it turned into the open refund question. Everything else in
> this ADR (the ownership shape, exactly-once with no gateway state, the off-by-default gate, the
> COD source-side deny) is unchanged.

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

### 1. When is the fee debited? — **at ACCEPT. The owner's call, not mine.**

~~My ruling was charge-at-completion.~~ **Overridden.** The debit fires in
`JeebOffersController.BuildAcceptedResponseAsync`, the single post-commit convergence point of the
one accept action (two route templates, one method — verified: the legacy `OffersController` accept
was deleted 2026-08-01 and is pinned dead by `LegacyOfferAcceptRouteRetiredTests`).

The fee is knowable there: 10% of the accepted price under Q-001, computed with
`WalletGuardContract.RequiredCommission` — literally the same expression the pre-commit accept guard
just checked the balance against, so what is checked and what is charged cannot drift.

**Exactly-once at accept, still with no gateway state.** The idempotency key is
`accept:{requestId}`, not `accept:{offerId}`:

- A replayed accept is a live risk, not a theoretical one. A retry **without** an `Idempotency-Key`
  header — the common mobile case — skips the gateway's idempotency middleware entirely and re-runs
  the whole post-commit block. Every pre-existing side effect there is convergent or merely noisy; a
  debit is the first that is neither. The wallet key is what stands between a jeeber and a double
  charge, and `A_Replayed_Accept_Charges_Exactly_Once` pins it with a control showing the same double
  creating two transactions when the key is not honoured.
- Keying on the **request** rather than the offer is deliberate. One request has exactly one winner,
  so a second accept carrying a *different* amount hits the same key with a different body and
  wallet-service answers **409 idempotency-conflict** — refused, not charged twice. An offer-scoped
  key would happily charge twice.
- The key is **derivable from any durable row that knows the delivery id**, so nothing has to be
  remembered anywhere to find the debit again.

I deliberately did **not** gate on `OfferAcceptWire.Replayed` (decoded upstream, currently unread).
It is true only when offer-service's own dedupe fires and says nothing about a gateway crash between
the debit and the response; using it would risk *never* charging a delivery whose first attempt died
early. The wallet key is the whole guarantee.

**Reserve-at-offer remains rejected** — and it is not what was asked for. The owner said drained on
*acceptance*, not held at *offer*. The evidence stands: wallet-service has **no hold API**, and its
de-facto hold (a `Pending` transaction header) **never expires** — no TTL, no sweeper — so reserving
on every losing bid would strand funds permanently and block wallet deactivation.

**What my rejected reasoning turned into.** I rejected accept-time because ~47% of deliveries cancel
(188 of 397 live rows) and a charge before completion needs a refund story. That did not evaporate;
it became the open question below. What *did* dissolve is the "no durable id at accept" objection —
the request id is durable and system-generated, and wallet-service's own idempotency index supplies
the uniqueness the settlement id was wanted for.

### The settle-time link replaces the settle-time debit

`settlement-service`'s `external_ref` hook keeps a real producer rather than reverting to the
"built, never called" state OA-30 criticised. The debit stamps wallet-service's generic
`ExternalReference` with `delivery:{requestId}`; at settle the gateway does a **pure read**
(`GET Transaction/by-external-reference/{ref}`) and stamps the transaction id onto the settlement
row. No money moves at settle.

A row that ends up **unstamped is exactly a delivery that settled with its fee never collected** —
`settlement.commission.unlinked`. That is the per-row, automatic version of the finding OA-30 had to
excavate by hand across 275 wallet holders.

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

## The open question this creates: cancellation

An accepted delivery that is later **cancelled keeps its fee taken. No refund is implemented and none
was invented** — the owner has not ruled on refunds, and inventing one would be exactly the silent
policy this programme exists to stop.

What *was* done is make the retention measurable rather than accidental.
`settlement.commission.retained_on_cancel` fires with a structured line naming the delivery, jeeber,
who cancelled and the retained amount. Cancellation never converged on a single seam the way accept
did, so all four gateway seams are instrumented: `CancellationService.CancelAsync` (client + jeeber),
`CancellationService.DecideAsync` (admin approval of a post-pickup cancel), the bare
`PATCH /deliveries/{id}/status` to `Cancelled`, and the legacy `DELETE /requests/{id}`. The fifth
route, `POST /admin/deliveries/{id}/transition`, is a pure relay with no gateway state write and is
**not** instrumentable from here — stated, not papered over. Before this change the gateway emitted
**no cancellation counter at all**.

**A refund is mechanically possible today, with no new primitive.** `PartnerWalletService`
already executes system→holder credits through the same sanctioned `Transaction/initiate` +
`execute` calls this debit uses, just with source and destination swapped, and a $5.00 credit
executed through wallet-service's public API during Phase V. So the owner is choosing a *policy*,
not funding an engineering project. It is **not enabled**, and no refund code was written.

Worth the owner knowing when deciding: the frozen delivery state machine already names its
`Ordered → Cancelled` client edge **`client_cancel_no_fee`**.

## What this does NOT do

- It does not backfill the 81 uncharged deliveries. 80 of them have no settlement row at all. That is
  owner-gated and untouched.
- It does not move any real money: nothing was deployed and the gate is off.
- It does not refund anything, ever. See the open question above.
- It does not make the mobile offer-composer copy true. *"$X is reserved from your wallet now ·
  charged only if you win · released if you're not picked"* and `fundingReserveBody` describe
  reserve-at-offer, which this ADR rejects. `JeebWalletProjection.ReservedNow` stays hard-coded `0`
  because nothing reserves anything. **The copy is false and must change** — raised as an OA; mobile
  is a separate repo and a store release is out of scope here.
- It does not resolve OQ1 (the wallet-guard fail-open/fail-closed question), which remains the
  owner's.
