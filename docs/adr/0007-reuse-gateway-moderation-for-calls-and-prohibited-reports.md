# 0007 — Reuse gateway moderation primitives for masked calls and prohibited-item reports

**Date:** 2026-06-20
**Status:** Accepted
**Deciders:** Mac Studio guild / mobile-2 backend lead
**Technical story:** Iter3 masked-call backend and prohibited-item-report backend closeout.

---

## Context and problem statement

The mobile masked-call button and Jeeber prohibited-item report affordance need real gateway
contracts. A reuse scan found no dedicated Olivium service for phone masking or proxy-number
session ownership, while `jeeb-gateway` already has a Twilio-ready `MaskedCallService`.
For prohibited reports, `feedback-service`, `compliment-service`, and `ban-service` do not own
the Jeeb prohibited-item moderation queue; the gateway already owns prohibited-item scanning and
`IFlaggedRequestStore` admin review.

## Decision drivers

1. Reuse existing Jeeb-owned moderation and call primitives before creating a new service.
2. Keep the mobile contract small and gateway-scoped.
3. Avoid routing prohibited-item reports into ban/review services that own different business
   concepts.

## Considered options

| Option | Summary |
| --- | --- |
| **A — Reuse gateway-native masked calls and flagged-request moderation** | Add the missing mobile-facing report route and keep calls on the existing gateway service. |
| **B — Reuse feedback/compliment/ban service** | Treat prohibited reports as feedback or enforcement inputs. |
| **C — Create a new calling/moderation service** | Net-new service for calls and prohibited reports. |

## Decision outcome

**Chosen option:** **A — Reuse gateway-native masked calls and flagged-request moderation**, because
it matches current Jeeb ownership: masked calls are a delivery-party BFF concern and prohibited-item
reports are reviewed in the same queue as scanner-generated flags.

**Confidence level:** Medium — the gateway implementation is intentionally minimal, and Twilio
Proxy credentials still need production enablement through environment configuration.

## Consequences

### Positive

- Mobile gets stable gateway routes without a new service or Docker dependency.
- Admins review scanner flags and manual Jeeber reports in one moderation queue.
- `feedback-service`, `compliment-service`, and `ban-service` are not overloaded with Jeeb
  prohibited-item semantics they do not own.

### Negative / trade-offs

- The gateway still has an in-memory flagged-request store until the state-service list/update
  primitives from ADR-0006 are available.
- The masked-call implementation remains Twilio-ready but not a full Twilio Proxy integration until
  live credentials and provider calls are enabled.

### Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| Gateway restart drops pending manual reports | Medium | Medium | Migrate `IFlaggedRequestStore` after ADR-0006 state-service primitives land. |
| Call button appears before Twilio is enabled | Low | Low | `MaskedCalls:Enabled=false` returns no session; mobile handles failure gracefully. |

## Links

- Supersedes: N/A
- Related: [ADR-0006](0006-gateway-in-memory-store-migration-to-state-service.md)
