# ADR-0012 — W5: a cancelled delivery gives the platform fee back

- **Status**: accepted, 2026-08-25
- **Programme**: wallet-guard-fix (wgf), wave W5, branch `epic/wallet-guard-fix`
- **Supersedes** the no-refund stance of [ADR-0011](0011-o1-commission-collection.md) — its
  "open question this creates: cancellation" and its "It does not refund anything, ever" bullet.
  Everything else in ADR-0011 (debit at accept, `accept:{requestId}`, off-by-default, COD deny,
  the settle-time link) stands unchanged.
- **Authority**: owner decision **OD-P1 `p1-refund-first`** (2026-08-24) — *"refund path before
  fees flip on"* — recorded as `exec/RULINGS.md` item 15; naming FROZEN in `exec/CONTRACT.md` §4;
  mechanism in `exec/DECISION-holds-mechanism.md` Op 3/Op 4/Op 5; design in
  `exec/_evidence/w5/DESIGN.md`.

## Context

ADR-0011 shipped the collection machine and then said, plainly, that a cancelled delivery keeps its
fee and that no refund was invented because the owner had not ruled. It also priced the question:
~47% of deliveries cancel (188 of 397 live rows), and the delivery state machine already names its
`Ordered → Cancelled` client edge **`client_cancel_no_fee`**. The retention counter
(`commission.cancel.retained`) existed precisely so the decision could be made from a number.

The owner has now ruled. OD-P1 orders the refund path **built before** `CommissionCollection:Enabled`
is ever flipped on, so that no delivery is ever charged in a world where the refund does not exist.
This ADR is that ruling written down; W5 is the code.

**The flag stays `false`.** Nothing here starts moving money. That is a property of the design, not
of the deployment — see *Money-neutrality while the flag is off* below.

## Decision 1 — the cancellation taxonomy is closed, and every path that can strand money is hooked

ADR-0011 instrumented four cancel seams for telemetry. W5 re-enumerated **every** way a request or
delivery can die after an offer is accepted, from code on this branch. "Fee state" at the moment a
path fires is: **flag OFF** → the winner's hold was already released at accept
(`hold.release reason=accept-collection-disabled`, `Controllers/V1/JeebOffersController.cs:701–706`)
and every loser's at `superseded` (`:737`, `ReleaseSupersededHoldsAsync` `:1095`); **flag ON** → the
winner's hold was capture-converted into the executed debit `accept:<requestId>` under external
reference `delivery:<requestId>` (`:670–680`, `Financials/CommissionCollector.cs:211–219`).

| # | Path | Where (file:line) | Terminal write | Money hooks before W5 | W5 action |
|---|---|---|---|---|---|
| P1 | Client cancel pre-pickup (canonical `Ordered`) | `Requests/Cancellation/CancellationService.cs:306–340`; route `Controllers/DeliveriesController.cs:1212–1217` | immediate `cancelled` | `ReleaseBidderHoldsAsync` → `ReleaseForRequestAsync(…, "request-cancelled")` at `CancellationService.cs:330/187–203`; retention counter `:329` | + post-capture refund |
| P2 | Client cancel post-pickup (`Picked`/`InTransit`) | `CancellationService.cs:342–373` | parks on `cancellation_requested` — not dead yet | none (correct: nothing terminal happened) | none — money moves at P3 |
| P3 | Admin APPROVES a P2 park | `CancellationService.DecideAsync:392–431` (approve `:410`, retention `:425`); controller `Controllers/AdminCancellationsController.cs:114` | `cancellation_requested → cancelled` | **nothing** — no release, no refund | + release (defensive) + post-capture refund |
| P4 | Jeeber cancel (`Ordered`…`AtDoor`) | `CancellationService.cs:245–301` (store commit `:253–260`); 3+/7d restriction `:284–289` | immediate `cancelled` | retention counter only `:291`; **no release** | + release (defensive) + post-capture refund |
| P5 | Legacy client `DELETE /requests/{id}` (any non-terminal state, incl. accepted) | `Controllers/RequestsController.cs:408–469` (`SetStatusAsync(Cancelled)` `:437`) | immediate `cancelled` | `ReleaseForRequestAsync(…, "request-cancelled")` `:458`; retention `:449` | + post-capture refund |
| P6 | Bare status PATCH → `Cancelled` (customer leg, admin `admin_resolve`/escalation legs) | `Controllers/DeliveriesController.cs` PATCH — retention seam `:883–890`, upstream commit `CanonicalTransitionAsync` `:897/:919`, local mirror `:937` | delivery-service row terminal; gateway mirrors | retention counter only, and it fires PRE-commit (pre-existing quirk) | + POST-commit release (defensive) + post-capture refund, gated on `canonical(upstream.Status) == Cancelled` |
| P7 | Admin transition relay `POST /admin/deliveries/{id}/transition` | `Controllers/AdminDeliveriesController.cs:107–148` | pure relay; the gateway writes nothing | none — **not hooked by choice** (a best-effort hook is technically possible; see Decision 7) | **NOT hooked.** Accepted gap; remedy is the manual runbook procedure (Decision 7) |
| P8 | Timeout / expiry | `Requests/RequestExpiryObserver.cs:121` (`TryExpireAsync`) | `expired` | `ReleaseForRequestAsync(…, "expired")` `:159` | none needed — expiry only fires on un-accepted auctions, so no capture can exist; release wired in W3 |
| P9 | KYC/admin action on the jeeber (ban, restriction, auto-offline, unregister) | `Auth/OtpSignIn/UsersMeController.cs:461`, `Availability/AutoOfflineSweeper.cs:97`, `Requests/Cancellation/BanServiceJeeberRestrictionStore.cs:95–99` | withdraws OFFERS (pre-accept); no path kills an accepted delivery | `ReleaseWithdrawnForJeeberAsync(…, "auto-offline")` | none needed — a banned jeeber's accepted delivery dies via P4/P6/P7, which are covered above |

Two honest notes that belong in the record rather than in a commit message:

- **Pre-accept cancel does reach the backend now.** Request-keyed routes exist
  (`POST /v1/requests/{id}/cancel`, `DELETE /v1/requests/{id}`, unversioned twin —
  `DeliveriesController.cs:1212–1217`) and the service accepts `scheduled/pending/matched`
  (`CancellationService.cs:59–65,134–135`), releasing every bidder's hold. The sprint-009 note "no
  backend pre-accept cancel route" is true only of **older mobile builds**, which cancel locally and
  never call the gateway; for those the backend net is P8 expiry plus the W3 sweeper. Either way no
  capture can exist pre-accept, so pre-accept death is release-only and already covered.
- **The release hooks stay on post-accept paths even though accept normally releases everything.**
  `ReleaseForRequestAsync` releases LIVE offers, and `accepted` counts as live
  (`Availability/PendingOffer.cs:41`). If the accept-time flag-off release FAILED (WARN at
  `JeebOffersController.cs:1110–1120`), the winner's hold survives on a live-status offer — which the
  sweeper's ORPHAN branch will not collect (the offer is not terminal) and whose MISSING branch
  believes is correctly collateralised. The cancel-seam release is the only prompt drain for that
  leak, and abort is idempotent (DECISION Op 3), so a double release is safe by construction.

## Decision 2 — the refund decision is keyed on the LEDGER, never on the flag

The question the code asks at a cancel is **"does an EXECUTED capture exist under external reference
`delivery:<requestId>`?"** — read from wallet-service. It is *never* "is `CommissionCollection:Enabled`
true right now?".

This is the load-bearing choice of the whole design:

- The flag can flip between capture and cancel. A delivery accepted while the flag was ON and
  cancelled after it was turned OFF has real money sitting in the platform wallet. A flag-keyed
  refund would look at `Enabled == false`, conclude "we don't charge fees", and **strand the
  jeeber's money permanently** — silently, which is exactly the class of failure this programme
  exists to end.
- The reverse transition is equally wrong in the other direction: a delivery accepted while OFF has
  nothing captured, but a flag-keyed refund reading `Enabled == true` at cancel time would try to
  credit a fee that was never taken — inventing money.
- The ledger cannot lie about either. It is the same durable marker ADR-0011 already relies on for
  exactly-once (`GET Transaction/by-external-reference/{ref}` is the settle-time link's read), and
  it is derivable from any row that knows the delivery id, so nothing new has to be remembered.

Consequence: **there is no branch on the flag anywhere in the refund path.** One code path is
correct in both states, which is also what makes the flag-off money-neutrality provable rather than
merely configured.

## Decision 3 — two regimes, one per fee state

### 3a. Pre-capture → release the hold. Refund by construction.

If no executed capture exists, the money was never taken; what may exist is a *hold* (a wallet-service
pending header, which nets out of the balance read). Releasing it **is** the refund, and no new
mechanism is required: every seam calls
`IHoldManager.ReleaseForRequestAsync(requestId, "request-cancelled", ct)`
(`Financials/Holds/HoldManager.cs:196`) inside a never-throws wrapper.

Naming is frozen by CONTRACT §4: structured event `hold.release`, reason **`request-cancelled`**, for
every cancel seam **regardless of who cancelled**. The frozen reason enum has no jeeber or admin
variant and none may be invented. A failed abort keeps the `wgf:hold:` intent record and the W3
sweeper retries it (`HoldManager.cs:180–187`).

### 3b. Post-capture → a compensating credit. FROZEN naming, byte-for-byte.

If an executed capture exists, the money moved and cannot be un-moved; wallet-service has no reversal
primitive and none was asked for. The refund is therefore a **second, compensating transaction** —
one credit, in the same shape ADR-0011 already noted was mechanically possible with no new primitive
(`PartnerWalletService` executes system→holder credits through the same sanctioned saga).

Frozen by `exec/CONTRACT.md` §4; none of these may drift:

| Field | Value | Why |
|---|---|---|
| Idempotency key | `refund:<requestId>` | pairs with the capture's `accept:<requestId>`; one request has exactly one winner, so one refund |
| External reference | `delivery:<requestId>` | **the same ref as the capture debit** — the ledger pairs debit and credit under one reference, and one read classifies both |
| Tag | `platform-fee-refund` | distinct from the capture's `platform-fee` (`CommissionCollector.cs:27`) and from the hold's `hold` |
| Service name | `jeeb-gateway` | client constant (`WalletCommissionDebitClient.cs:161`) |
| Legs | ONE: source `__SYSTEM__` platform wallet → destination = the jeeber's spendable USD fee wallet | the capture's legs **read off the capture transaction and swapped**, never re-resolved |
| Amount | the captured commission **verbatim** | never recomputed from the fee |
| Flags | `applyConfiguredFees: false`, `isAdditionalFees: false` | the caller supplies the complete entry; wallet-service must not append a fee to a fee |
| Saga | `Transaction/initiate` → `Transaction/execute` | the same two-phase call the capture uses |
| Structured log | `fee.refund` (requestId, jeeberId, amount, txId, cancelledBy) | one greppable line per refund |

Two of those deserve their reasoning stated, because they are where a plausible shortcut would have
introduced a bug:

- **Legs are swapped, not re-resolved.** Re-resolving the jeeber's wallet at refund time would
  misroute the credit if the wallet set changed between accept and cancel (new wallet provisioned,
  currency wallet added). Reading the capture's own legs and reversing them makes the credit land
  exactly where the debit came from, and makes OD-C3-5 ("wallets are NEVER combined") hold trivially
  — there is nothing to combine.
- **Amount is the captured amount, not `RequiredCommission(fee)` recomputed.** This is the same
  discipline ADR-0011 applied to the debit (`Settlement.Total` verbatim). An edited fee, a rate
  change, or a rounding change cannot make what is refunded differ from what was charged.

## Decision 4 — idempotency: exactly one refund per delivery, replays safe

Three independent layers, so no single one has to be perfect:

- **L1 — wallet-service.** The unique index on `Idempotency-Key` + server-computed request
  fingerprint. A replayed `initiate` under `refund:<requestId>` returns the **original** transaction;
  a divergent body under the same key is a 409 `idempotency-conflict`, never a silent second credit.
  This is the same guarantee ADR-0011 leans on for the debit.
- **L2 — the ledger pre-check.** Before any write, the refunder reads `delivery:<requestId>`; an
  executed header already tagged `platform-fee-refund` short-circuits to `fee.refund.replay` and
  **zero** mutation calls.
- **L3 — the store layer.** Double-cancel is impossible: `TryCancelAsync` / `TryDecideCancellationAsync`
  refuse a re-transition, so a seam invokes the refunder at most once per committed terminal
  transition. The sweeper is the only other re-driver, and it replays the same key.

## Decision 5 — failure posture: never fail the cancel; reconcile in the sweeper

A cancellation is a user-facing outcome that has already been decided by the time money is touched.
It never fails, degrades, or blocks on a wallet call. `IFeeRefunder.RefundOnCancelAsync` **never
throws**, and every seam wraps it in the same try/WARN wrapper as `ReleaseBidderHoldsAsync`
(`CancellationService.cs:187–203`); the outcome the user sees is computed before and independently of
any wallet call.

That would be a licence to lose refunds if it stood alone, so it does not. A durable **refund intent**
(state-service KV, key prefix `wgf:refund:`, append-only revision chain, tombstone = closed — the
exact mechanics of `Holds/HoldIntentStore.cs`) is written **before** the credit (discipline I2: no
money movement may exist that the sweeper cannot find), and the W3 `HoldSweeper` gains a refund pass
that re-drives open intents each `Holds:SweepIntervalSeconds`.

| Failure | Behaviour |
|---|---|
| Ledger read fails (transport) | intent `open`, log `fee.refund.deferred`, cancel unaffected; sweeper retries |
| No executed capture found | `fee.refund.skipped reason=no-capture`, **zero mutation calls** |
| Refund already executed | `fee.refund.replay`, intent closed, no second credit |
| `initiate` 409 `idempotency-conflict` | accounting divergence: ERROR, counter, intent → `conflict` — **reported every sweep, never blind-retried** |
| `initiate`/`execute` deterministic rejection | abort the pending header (money did not move), intent stays `open`, sweeper retries |
| `execute` ambiguous (5xx / timeout) | **deliberately NOT aborted** — ADR-0011's double-move rule. Intent stays `open`; the retry replays `refund:<requestId>`, wallet-service returns the original transaction, and execute is idempotent on the tx id |
| Intent write fails | still attempt the credit (idempotency makes it safe); any later failure logs ERROR + `fee.refund.failures` — never silent |

No partial refund and no double refund is constructible: one single-leg transaction, one frozen key
per delivery, and the only re-driver replays that key.

## Decision 6 — the actor does not change the money. Deterrence lives in the restriction policy.

**A jeeber who cancels gets the fee back, exactly like a client cancel or an admin-approved cancel.**
The refund is uniform across P1/P3/P4/P5/P6, and the release reason is the single frozen
`request-cancelled` for all of them.

The tempting alternative — retain the fee when the jeeber is at fault — is rejected on three grounds:

1. It is a **penalty invented by an implementer**. OD-P1 ruled that a cancelled delivery gives the
   fee back; it drew no actor distinction, and inventing one is the silent-policy failure mode
   ADR-0011 refused for refunds in the first place.
2. jeeb **already has** a jeeber-cancellation deterrent, and it is a real one that predates this
   epic: 3-or-more cancellations in 7 days restricts the jeeber
   (`CancellationService.cs:284–289`, `BanServiceJeeberRestrictionStore.cs:95–99`). Deterrence is
   enforced by access to work, not by keeping 10% of one fee.
3. Fee retention as a penalty is not attributable at the seam. P6 and the admin legs cannot reliably
   say who is at fault, so a fault-keyed rule would be applied inconsistently across paths — which is
   worse than no penalty.

The retention telemetry stays wired at every seam, but its meaning changes: `commission.cancel.retained`
/ `CommissionRetainedOnCancel` now measures **"a fee was touched by a cancel"**, not "a fee was kept
forever". ADR-0012 renames the interpretation, not the counter — the counter's own source comment in
`Financials/CommissionRetention.cs` still describes the ADR-0011 world and is superseded by this ADR.

## Decision 7 — the P7 admin-relay gap is NOT HOOKED BY CHOICE, with a written remedy

`POST /admin/deliveries/{id}/transition` (`Controllers/AdminDeliveriesController.cs:107–148`) is a
pure relay: the gateway forwards the transition to delivery-service and writes nothing of its own, so
it has no post-commit seam of its own to hook. It is **not** literally uninstrumentable — the relay
does know the requested `To` state and does observe the upstream 2xx
(`AdminDeliveriesController.cs:150–176`), so a best-effort hook was technically available and was
**declined**, not ruled out. ADR-0011 used the stronger "not instrumentable" wording for the
retention counter on this same route; this ADR corrects that framing and supersedes it.

This is **stated, not papered over**:

- It is an **admin-only** surface. It is not reachable by clients or jeebers, so it cannot be used to
  strand an ordinary user's money without an admin doing it.
- Hooking it means the gateway inferring a terminal cancel from a relayed request plus a status code
  — mirroring upstream state on a relay, which re-introduces exactly the divergence ADR-0009 fought
  — or a delivery-service change, which is out of this epic's scope. On a break-glass route that
  trade is not worth a money-moving inference.
- The remedy is a **manual refund procedure**, written for the owner in
  `exec/RUNBOOK-holds-abort.md` §R: the same frozen key, reference and tag executed by hand over the
  wallet HTTP API, followed by the same verification. An admin who cancels through the relay refunds
  through the runbook.

Revisit this if the relay ever becomes a routine path rather than a break-glass one.

## Money-neutrality while `CommissionCollection:Enabled` is `false`

The flag remains `false` in `appsettings.json` and `appsettings.Production.json`. Merging W5 must not
start moving money, and this holds by construction rather than by configuration:

- While the flag is off, **no capture is ever executed**. Every accept releases the winner's hold
  (`reason=accept-collection-disabled`) and captures nothing, so no header tagged `platform-fee` can
  exist under `delivery:<requestId>`.
- The refunder's step 2 therefore always finds no executed capture, logs
  `fee.refund.skipped reason=no-capture`, and returns having made **zero** wallet mutation calls — no
  `initiate`, no `execute`, no `abort`.
- The credit branch is unreachable without an executed capture, which is unreachable without the
  flag having been on. The pre-capture branch is a hold **abort**, which is money-neutral by
  definition (only `execute` moves money).
- This is pinned by test, not asserted: `Refund_FlagOff_NoCapture_MakesNoWalletMutationCalls` records
  zero initiate/execute/abort, and `Cancel_FlagOff_NoHold_TouchesNoWallet` does the same for the whole
  seam.

The refund path is therefore **live and inert** — exactly what OD-P1 asked for: built, tested and
merged *before* the fee flip, so the flip has nothing left to invent.

## Consequences

- ADR-0011's "the open question this creates: cancellation" is **closed**. Its "does not refund
  anything, ever" bullet is superseded; a banner and pointer are recorded in that file.
- The delivery state machine's `client_cancel_no_fee` edge name is now true of the money, not just
  of the label.
- New surface owned by the gateway: `Financials/Refunds/FeeRefunder.cs`,
  `Financials/Refunds/RefundIntentStore.cs`, a richer ledger read on
  `Financials/WalletCommissionDebitClient.cs` (existing methods byte-identical), a refund pass in
  `Financials/Holds/HoldSweeper.cs`, and counters `fee.refund.credited`, `fee.refund.failures`,
  `fee.refund.skipped`.
- **No wallet-service change.** wallet-service stays generic and read-only to this programme; the
  refund uses only its existing idempotent transaction primitives, as Decisions 1–4 of
  `ouday-wallet-decison.md` require.
- No new mobile surface (CONTRACT §4): a wallet balance refresh already shows the credit.
- New operational duty: `fee.refund.conflict` intents are reported every sweep and are never
  auto-resolved. A conflict means the same refund key was used with different money and needs a
  human.

## What this does NOT do

- It does not turn fee collection on. `CommissionCollection:Enabled` stays `false`; the flip is a
  separate, owner-gated step with its own W7 runbook.
- It does not refund the 81 historically uncharged deliveries or backfill anything. Nothing was ever
  charged there.
- It does not hook P7 (Decision 7), and does not pretend to.
- It does not add a partial or pro-rated refund. A cancelled delivery refunds the captured commission
  in full or nothing; there is no policy input for anything else.
- It does not change the accept-time debit, its key, or the settle-time link (ADR-0011 stands).
- It does not penalise cancelling jeebers through the wallet (Decision 6).

## Test anchors (canonical; TESTING.md discipline — named, fails-before, passes-after, wall-clock-free)

- Per-path: `ClientCancelPrePickup_PostCapture_CreditsCapturedFee_Once`,
  `JeeberCancel_PostCapture_CreditsCapturedFee`, `AdminApproveCancellation_PostCapture_CreditsCapturedFee`,
  `LegacyDeleteRequest_PostCapture_CreditsCapturedFee`, `StatusPatchCancelled_PostCapture_CreditsCapturedFee`,
  `Cancel_PreCapture_ReleasesLiveHold_MovesNoMoney`, `Cancel_RefundFailure_DoesNotFailCancellation`.
- Idempotency: `Refund_DoubleCancel_CreditsExactlyOnce`, `Refund_ReplaySeesExistingRefundTag_NoSecondInitiate`,
  `Refund_AmountIsCapturedAmount_NotRecomputed`, `Refund_ExecuteAmbiguous_NotAborted_IntentStaysOpen`,
  `Refund_IdempotencyConflict_MarksConflict_NoCredit`.
- Flag-off: `Refund_FlagOff_NoCapture_MakesNoWalletMutationCalls`, `Cancel_FlagOff_NoHold_TouchesNoWallet`.
- Sweeper: `Sweep_RetriesOpenRefundIntent_UntilCredited`, `Sweep_LostCompletion_ReplayConverges_ClosesIntent`,
  `Sweep_ConflictIntent_ReportedNotRetried`, `Sweep_NoRefundServices_SkipsQuietly`.
