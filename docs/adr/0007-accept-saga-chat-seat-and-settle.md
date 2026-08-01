# ADR-0007 — Post-accept chat: one seat-and-settle call, plus a reconciler

Status: accepted (GW5 / W1.6-gateway)
Supersedes: nothing. Extends the S03 P1 post-accept chat-readiness path.

## Context

`JeebOffersController.BuildAcceptedResponseAsync` runs a **post-commit** block: by the
time it executes, the offer-service accept saga has already committed the single-winner
transition. Inside that block the gateway made the winner's chat thread usable with **two**
independent chat-service requests:

```
POST  /api/conversations/{id}/participants     # seat the winner
PATCH /api/conversations/{id}/phase            # advance phase + promote + remove losers
```

Both were wrapped in one `try/catch` that logged
*"jeeber {JeeberId} may read 403 on chat until reconciled"* and swallowed.

Three problems, in increasing order of severity:

1. **The half-state.** First call lands, second does not → the winner sits in a
   conversation still in its pre-settlement phase with every losing bidder still active.
   chat-service only grants the winner visibility of the client's messages in a clean 1:1
   (owner + single accepted counterpart), so that roster is simultaneously a leak risk and
   the reason the winner reads a blank thread.
2. **Nothing reconciled.** The log line named a reconciliation that did not exist.
3. **No signal.** One `LogWarning` nobody aggregates. Accept is the money-committing step
   and chat is the only coordination channel a cash handover has.

"Fail loud" is not available here. The saga has committed; there is nothing left to abort,
and turning a committed accept into a 5xx tells the customer their successful auction
failed.

## Decision

**1. One call.** `IJeebConversationClient.SettleAsync` →
`POST /api/conversations/{id}/settle`, chat-service's additive endpoint from CB4. It
seats the winner, sets the phase and soft-removes the losing bidders against one loaded
aggregate, one store write, one projection reconcile. The frozen contract is
`chat-service/documentation/CONVERSATION-SETTLE-CONTRACT.md`.

**2. One place.** `IAcceptChatSettler` owns resolve-or-create → stamp → settle, because
the accept path and the reconciler must perform an identical step and a second copy of a
money-adjacent saga step is a second place to drift. It **throws**; the accept path
swallows (degrade-don't-fail, unchanged) and the reconciler counts and retries.

**3. A reconciler.** `AcceptChatSettleReconciler`, a hosted sweep on the
`SettlementLedgerReconciler` precedent. Candidates are re-derived from the gateway's own
**durable request rows** — any request with an assigned jeeber inside the look-back
window. Divergence is decided by asking **chat-service**, the membership authority:
settled phase, winner active with the winner role, active roster exactly {owner, winner}.

**4. Counters, not just logs.** `chat.accept_settle.{settled,failures,reconcile_divergent,reconciled}`
on the meter the Prometheus exporter already scrapes.

## Why not an outbox table

An outbox row would be written at the top of the chat step. A kill landing one instruction
earlier loses it. The durable request row is written by the accept projection
(`SetStatusAsync` / `SetJeeberIdAsync`) **before** the chat step runs, so it survives a
kill anywhere inside that step. The outbox defends against strictly less, and costs a
table.

## Why not a gateway-local "settled" marker

A marker is one more write that can be lost, and a lost marker is indistinguishable from a
lost settle. Asking chat-service costs one cheap read per candidate and cannot be wrong
about its own roster.

## Why the candidate filter is `JeeberId`, not `status == accepted`

A delivery that has progressed to `picked_up` / `at_door` still needs a settled 1:1 chat. A
status filter would abandon exactly the deliveries furthest along.

## Consequences

- `AddParticipantAsync` and `AdvancePhaseAsync` remain on the interface with unchanged
  behaviour. The **offer-submit** seat (`RequestOffersController`) still uses the
  participants route — seating a bidder mid-auction is not a settlement.
- `JeebOffersController` no longer holds `IJeebConversationClient`.
- `IRequestsStore` / `IDurableRequestsMirror` each gain one additive read,
  `ListAssignedSinceAsync`. On the durable store it reads the **Postgres mirror**: an
  inner-store-only read would answer "nothing to reconcile" after exactly the bounce the
  reconciler exists to heal.
- **Deploy ordering matters.** `POST /settle` must exist upstream before a gateway
  carrying this change settles anything. chat-service `origin/main` has carried it since
  `9305aea` (CB4, PR #98). Deploying this gateway against an older chat-service makes
  every settle 404 — loudly, via `chat.accept_settle.failures`, and the reconciler will
  keep retrying rather than corrupting anything. There is deliberately **no fallback to
  the two-call sequence**: a silent fallback would reintroduce the exact window this
  change removes, and would hide the version skew that caused it.
- **First sweeps after deploy also heal history.** Every accepted request inside the
  look-back window is a candidate, so conversations broken by the pre-GW5 two-call
  sequence get repaired. Wanted, and bounded by `PageSize`.

## What this does NOT claim

Everything asserted in this repository for GW5 was measured on the in-process host
(`tests/JeebGateway.IntegrationTests/Gw5Pack`) with a chat-service **double**. No live
chat-service, no Firestore path, no device took part. The claim that a settled winner can
actually read the thread on a phone is proved on the live two-service round (RS), not
here.

Atomicity remains per-request. One call replaces two, which removes the window between two
writes; it does not make the accept saga's commit and this call a single transaction. That
is what the reconciler is for.
