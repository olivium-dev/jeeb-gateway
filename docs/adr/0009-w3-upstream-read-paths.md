# ADR-0009 — W3 mode flags must own a real read path or refuse the rung

- **Status**: accepted, 2026-08-16
- **Programme**: gateway-db-extraction (gwdbx), wave W3
- **Supersedes nothing.** Companion to [ADR-0008](0008-cms-config-leg-superseded-by-bundler.md).

## Context

A step-10 audit of the six W3 config legs found that only ONE — the prohibited-items lexicon — had
all three properties a cutover needs: data migrated, code reading upstream, flag flipped. The other
five had a validated ladder value, a mirror or a store class, and **no read consumer at all**. Their
flags could be moved to `dual-write-upstream-read` or `upstream-authority` and *nothing would
change*: the serving interface was bound to the in-memory store unconditionally, with no mode
branch and no boot guard to catch the mistake.

That is the failure this programme keeps repeating. A green flag that changes no behaviour is worse
than an unflipped one, because the register says the domain moved when it did not.

## Decision

**A W3 mode key may only exist in one of three states, and each is enforced in code:**

1. **Real** — the serving interface is bound behind the mode, the upstream read is implemented, and
   a `ValidateOnStart` guard refuses the read rung when the upstream is unwired. (lexicon + acks,
   flagged-requests, account-deletion, OTP-escalations)
2. **Refused** — the read path cannot be built because the OWNER does not expose the capability, so
   a `ValidateOnStart` guard refuses every rung at or above `dual-write-upstream-read` and names the
   missing endpoints. (availability)
3. **Pinned** — the leg is superseded and the key is kept only so a stale value fails loudly.
   (CMS, ADR-0008)

**No fourth state.** A mode key that is merely *validated* is not allowed to reach a read rung.

Two corollaries, both applied here:

- **Never silently fall back to local.** A read that cannot be answered upstream fails closed and
  names the upstream. The single exception is the create-time moderation gate, which fails OPEN by
  design — and it now falls open onto the last-known-good *published* snapshot, never onto a local
  store that is empty by design.
- **Import and parity resolve the LOCAL side explicitly.** Once a read rung is live, the serving
  interface *is* upstream; a tool that resolved it would read upstream, re-publish it to itself and
  report clean by construction. Local roots are reached through `ILocalProhibitedItemsStore` /
  `ILocalFlaggedRequestStore`, upstream through `IUpstreamFlaggedRequestStore`.

## Consequences

- **flagged-requests has ONE upstream.** The importer used to write state-service work-items of kind
  `content-flag` while `StateServiceFlaggedRequestStore` read cases of kind `moderation_review` —
  the store would have come up against an empty list with the imported rows in a surface it never
  queries. The case engine wins (it is the richer surface and carries the decision trail); the
  work-items leg is deleted, and parity now compares row-for-row instead of proving per-subject
  existence on a dead surface.
- **The lexicon leg's post-flip regressions are closed**: the admin catalog list and item-by-id now
  read the same published surface the gate enforces (they reported 0 items against a live 15-item
  lexicon), catalog authoring fails closed instead of writing rows nothing reads, and the ack ledger
  is genuinely upstream instead of an in-memory ledger that emptied on every bounce.
- **ProhibitedItemsMode is already `upstream-authority` live**, so these paths ARM AT THE NEXT
  GATEWAY RESTART, not at a future flip. Preconditions are recorded in the PR and the deletion
  ledger; they are owner-gated.
- **Availability stays gateway-authoritative (G-10).** `DeliveryServiceAvailabilityStore` throws
  `OwnerCapabilityUnavailableException` for the online-list and known-since-list reads that the
  admin ops-map and `AutoOfflineSweeper` both need. That is a missing delivery-service capability,
  not gateway wiring, so the rung is refused at boot until delivery-service ships it.
- **Backfills are still owed** for OTP-escalations and account-deletion. `StateServiceConfigImporter`
  covered lexicon, acks and flagged only and was deleted at ADR-0010 (its local source stores are
  process memory that authoring can no longer refill); there is no replay job for the other two. Per G-21 each must
  be a gateway-resident replay job POSTing to the owning service, never a cross-service DSN read —
  and each is a prerequisite for its leg's flip, not for this ADR.
