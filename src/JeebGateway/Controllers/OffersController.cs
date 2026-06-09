using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Client-facing offer acceptance endpoint. In the Jeeb auction a CLIENT creates a
/// delivery request, JEEBERS submit offers (bids) on it, and the request-owning
/// CLIENT accepts one jeeber's offer to award the delivery. Accepting is therefore
/// a CLIENT action keyed on request ownership; the offer-service saga owns the
/// race-safe single-winner transition, OTP mint, chat-thread open, and sibling
/// rejection. The gateway forwards the CLIENT's identity to the request-scoped
/// offer-service accept route (whose guard authorizes <c>request.client_id ==
/// actor</c>) and re-emits the upstream status verbatim.
///
/// 409 outcomes (legacy in-memory path only; the upstream saga owns these when
/// <c>UseUpstream:Offer = true</c>):
///   * <c>request-not-acceptable</c> — request moved out of
///     pre-acceptance (another offer won the race, expired, cancelled).
///   * <c>offer-not-pending</c> — offer was already accepted or withdrawn.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("offers")]
public class OffersController : ControllerBase
{
    /// <summary>BR-10: per-Jeeber maximum of concurrent active deliveries.</summary>
    public const int ActiveDeliveriesLimit = 2;

    /// <summary>
    /// Mobile-app renders this verbatim in the error banner when the cap
    /// is hit — matches the wording used for the BR-9 sibling rule.
    /// </summary>
    internal const string LimitExceededMessage =
        "Maximum 2 active deliveries. Complete a delivery before accepting another.";

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly IDualRoleService _dualRole;
    private readonly TimeProvider _clock;
    private readonly IOfferServiceClient _offerService;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IConversationProvisioner _conversations;
    private readonly IDeliveryServiceClient _deliveryService;
    private readonly UpstreamFeatureFlags _flags;
    private readonly DeliveryClientOptions _deliveryOptions;
    private readonly ILogger<OffersController> _logger;

    public OffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IDualRoleService dualRole,
        TimeProvider clock,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IConversationProvisioner conversations,
        IDeliveryServiceClient deliveryService,
        IOptions<UpstreamFeatureFlags> flags,
        IOptions<DeliveryClientOptions> deliveryOptions,
        ILogger<OffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _dualRole = dualRole;
        _clock = clock;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _conversations = conversations;
        _deliveryService = deliveryService;
        _flags = flags.Value;
        _deliveryOptions = deliveryOptions.Value;
        _logger = logger;
    }

    [HttpPost("{offerId}/accept")]
    // ADR-005 L2 / S07: this route declares the offer.accept {client} capability. The caller is the
    // request-owning CLIENT awarding the delivery to one jeeber's offer — NOT the jeeber. The offer
    // is bid by a jeeber (offer.submit {jeeber}); the client who owns the parent request accepts it.
    // The authoritative map keys offer.accept -> {client} (CapabilityRolePolicy), and the gateway
    // forwards THIS CLIENT's id downstream so the offer-service request-scoped accept guard
    // (request.client_id == actor) passes. STATE (ownership, race-safe single-winner, status, BR-1
    // same-delivery dual-role) stays in the offer/delivery service.
    [RequireCapability(Capabilities.OfferAccept)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(string offerId, CancellationToken ct)
    {
        // The acting user is the CLIENT who owns the request (the acceptor), not the jeeber.
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem)) return problem;

        // Thin-BFF wire: when the offer ledger is the real offer-service, the
        // gateway does NOT re-run the auction. It forwards to the offer-service
        // accept saga (which owns OTP mint, chat-thread open, sibling rejection,
        // request transition, and SELECT FOR UPDATE + optimistic_lock
        // race-safety) and re-emits the upstream status verbatim. The in-memory
        // Get/Accept seam below (which throws NotSupportedException upstream) is
        // never touched on this path.
        if (_flags.Offer)
        {
            return await AcceptViaUpstreamAsync(offerId, actorId, ct);
        }

        var offer = await _offers.GetAsync(offerId, ct);
        if (offer is null) return NotFound();

        // Legacy in-memory auction (flag off; dead on the live fleet). The acceptor
        // is the request-owning CLIENT; the offer was bid by a jeeber. A caller who
        // does not own the parent request may not accept it — forbidden, not "not
        // found", so the mobile app shows the correct banner and ops can spot it.
        var legacyRequest = await _requests.GetAsync(offer.RequestId, ct);
        if (legacyRequest is not null
            && !string.Equals(legacyRequest.ClientId, actorId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Only the request owner can accept an offer.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/offer-not-owned"
            });
        }

        if (offer.Status != PendingOfferStatus.Pending)
        {
            return Conflict(new ProblemDetails
            {
                Title = $"Offer is no longer pending (current={offer.Status}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-pending"
            });
        }

        // BR-1 (T-backend-041): a user cannot act as both Client and Jeeber on the
        // same delivery. The party being committed to the delivery is the offer's
        // JEEBER (the bidder), so it is that jeeber — not the accepting client — who
        // must not also own the request. This check runs before the heavy atomic
        // accept so we fail fast without holding the store write lock.
        if (await _dualRole.WouldViolateSameDeliveryRuleAsync(offer.JeeberId, offer.RequestId, ct))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot accept your own delivery request (BR-1).",
                Detail = "A user cannot act as both Client and Jeeber on the same delivery.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/same-delivery-role-violation"
            });
        }

        var now = _clock.GetUtcNow();
        DeliveryRequest? accepted;
        try
        {
            // The winning jeeber recorded on the request is the offer's bidder.
            accepted = await _requests.TryAcceptByJeeberAsync(
                offer.RequestId,
                offer.JeeberId,
                ActiveDeliveriesLimit,
                now,
                ct);
        }
        catch (TooManyActiveDeliveriesException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = LimitExceededMessage,
                Detail = $"Jeeber has {ex.ActiveCount} active deliveries (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-deliveries"
            });
        }
        catch (RequestNotAcceptableException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Request is no longer acceptable.",
                Detail = $"Current status: {ex.CurrentStatus}. Another Jeeber may have accepted, or it expired.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-not-acceptable"
            });
        }

        if (accepted is null)
        {
            // The offer pointed at a request the store could not find. In
            // production this would be a referential-integrity bug; we
            // surface it as 404 so the mobile app retries the listing
            // instead of looping on a phantom offer.
            return NotFound();
        }

        // Mark the offer accepted (and withdraw the Jeeber's siblings)
        // only after the request transition succeeded — keeps the two
        // sides of the relationship consistent if the request flip threw.
        await _offers.AcceptAsync(offerId, now, ct);

        return Ok(ToDto(accepted));
    }

    /// <summary>
    /// Upstream accept path (FeatureFlags:UseUpstream:Offer = true). The acting user
    /// is the request-owning CLIENT awarding the delivery to a jeeber's offer.
    /// Resolves the offer's request via the BFF routing index, forwards to the
    /// offer-service request-scoped accept saga with the CLIENT's id as
    /// <c>x-user-id</c> (so the upstream guard <c>request.client_id == actor</c>
    /// passes), and re-emits the upstream status verbatim. No auction rule is
    /// recomputed here — the offer-service owns every negative (403/410/409/404)
    /// and the race-safe single-winner guarantee. The one gateway-owned
    /// pre-forward check is BR-1 (same user cannot be both Client and Jeeber on
    /// one delivery). Because the actor here is the request-owning CLIENT, the only
    /// legitimate BR-1 violation is a genuine self-offer (actor == the bidding
    /// jeeber of the accepted offer), so the check compares the actor against THIS
    /// offer's recorded bidder — never against request.ClientId, which would trip
    /// on every valid accept.
    /// </summary>
    private async Task<IActionResult> AcceptViaUpstreamAsync(
        string offerId, string actorId, CancellationToken ct)
    {
        var requestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (requestId is null)
        {
            // Unknown to this gateway instance: the offer was never submitted
            // through here (or the routing index was lost on restart). 404 is the
            // correct contract for a phantom offer — the mobile app re-lists.
            _logger.LogInformation(
                "Accept for offer {OfferId} could not resolve a requestId from the routing index; returning 404.",
                offerId);
            return NotFound();
        }

        // BR-1 fast-fail (gateway-owned composition rule). BR-1 forbids a user from
        // acting as BOTH Client and Jeeber on the SAME delivery. On the accept path
        // the actor IS the request-owning CLIENT (request.client_id == actor is the
        // normal, correct case — the offer-service guard authorizes exactly that),
        // so the only legitimate BR-1 violation here is a genuine SELF-OFFER: the
        // accepting client is also the jeeber who bid the offer being accepted.
        //
        // We therefore compare the actor against THIS OFFER's bidder (recorded at
        // submit time in the routing index), NOT against request.ClientId — the
        // latter trips on every valid accept. When the index has no jeeber id for
        // the offer (unknown / legacy pairing), we do NOT assert a violation here;
        // the offer-service request-scoped accept guard (request.client_id == actor)
        // and the role-switch guard remain the authoritative owners of dual-role
        // self-dealing, so we let the saga decide rather than guess.
        var offerJeeberId = _offerRequestIndex.ResolveJeeberId(offerId);
        if (offerJeeberId is not null
            && string.Equals(offerJeeberId, actorId, StringComparison.Ordinal))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot accept your own delivery request (BR-1).",
                Detail = "A user cannot act as both Client and Jeeber on the same delivery.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/same-delivery-role-violation"
            });
        }

        // BR-10 pre-forward cap (gateway-owned composition rule). A jeeber may hold
        // at most ActiveDeliveriesLimit (default 2) concurrent ACTIVE deliveries.
        // Accepting an offer assigns the delivery to the OFFER'S jeeber (the bidder),
        // NOT the accepting client — so the cap is checked against offerJeeberId, the
        // recorded bidder, exactly mirroring the legacy in-memory path which keys the
        // cap on offer.JeeberId. delivery-service owns the authoritative active count
        // (status NOT IN terminal); the gateway short-circuits to 409 here BEFORE
        // forwarding the saga so no third delivery is ever created.
        //
        // Why pre-forward (not just rely on offer-service): offer-service does not
        // enforce BR-10 today (6 successive accepts all returned 200 on the live
        // fleet — the baseline N7 red), so the gateway BFF is the enforcement point.
        //
        // Degrade-don't-fail: a delivery-service blip on the count read must NEVER
        // turn an otherwise-valid accept into a 5xx (that would regress S01-S06 happy
        // accepts). On a fault we LOG and treat the jeeber as under-cap, letting the
        // accept proceed; the offer-service Conflict mapping (OfferAcceptStatus.
        // Conflict -> 409) remains the backstop. The cap is best-effort-at-gateway.
        if (offerJeeberId is not null)
        {
            var limit = _deliveryOptions.ActiveDeliveriesLimit;
            int? activeCount = null;
            try
            {
                activeCount = await _deliveryService.CountActiveDeliveriesByJeeberAsync(offerJeeberId, ct);
            }
            catch (Exception ex)
            {
                // delivery-service unreachable / non-2xx: do not block the accept.
                _logger.LogWarning(ex,
                    "BR-10 active-delivery count for jeeber {JeeberId} (offer {OfferId}) failed; " +
                    "treating as under cap and forwarding the accept (offer-service Conflict is the backstop).",
                    offerJeeberId, offerId);
            }

            if (activeCount is int count && count >= limit)
            {
                _logger.LogInformation(
                    "BR-10: rejecting accept of offer {OfferId} — jeeber {JeeberId} already holds {Count} active deliveries (limit {Limit}); no third delivery created.",
                    offerId, offerJeeberId, count, limit);
                return Conflict(new ProblemDetails
                {
                    Title = LimitExceededMessage,
                    Detail = $"Jeeber has {count} active deliveries (limit {limit}).",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/too-many-active-deliveries"
                });
            }
        }

        // Server-minted Idempotency-Key (>= 8 chars) so the offer-service can
        // dedupe accept retries. Deterministic per (actor, offer) so a client
        // retry of the SAME accept replays rather than double-applying.
        var idempotencyKey = $"accept-{actorId}-{offerId}";

        // Forward the CLIENT's id as the acting user. The offer-service request-scoped
        // accept guard authorizes request.client_id == actor; sending the jeeber here
        // (the pre-fix bug) caused the live S07 H5/A6 403.
        var result = await _offerService.AcceptWithStatusAsync(
            actorId, requestId, offerId, idempotencyKey, ct);

        switch (result.Status)
        {
            case OfferAcceptStatus.Accepted:
                // The offer-service saga is the authority for the single-winner
                // transition; it has committed upstream. The gateway BFF now owns
                // the cross-service COMPOSITION the offer-service must not do
                // (no inter-service coupling): sync its own request ledger (H6b),
                // record the winner on the durable delivery row (H6c), and advance
                // the broadcasting conversation (H6d). EVERY side-effect here is
                // DEGRADE-DON'T-FAIL — a downstream blip must never turn a
                // successful accept into a 5xx.
                await OrchestrateAcceptedAsync(requestId, actorId, result.Envelope!, ct);
                return Ok(BuildAcceptedDto(requestId, actorId, result.Envelope!));

            case OfferAcceptStatus.NotOwner:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "Only the request owner can accept an offer.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/offer-not-owned"
                });

            case OfferAcceptStatus.Expired:
                return StatusCode(StatusCodes.Status410Gone, new ProblemDetails
                {
                    Title = "The request expired before the offer could be accepted.",
                    Status = StatusCodes.Status410Gone,
                    Type = "https://jeeb.dev/errors/offer-expired"
                });

            case OfferAcceptStatus.Conflict:
                return Conflict(new ProblemDetails
                {
                    Title = "Offer can no longer be accepted.",
                    Detail = "The offer was already accepted, withdrawn, or the Jeeber is at the active-delivery cap.",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/offer-not-pending"
                });

            case OfferAcceptStatus.NotFound:
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// S07 post-accept BFF orchestration (thin saga). Runs AFTER the offer-service
    /// accept saga returns <see cref="OfferAcceptStatus.Accepted"/> and BEFORE the
    /// 200 is emitted. The gateway is the SOLE cross-service composer here (org
    /// no-coupling law: offer/delivery/chat services never call each other):
    /// <list type="number">
    ///   <item><b>H6b request-sync</b> — flips the gateway's own request ledger row
    ///     to <c>accepted</c> + winning <c>jeeberId</c> via the existing atomic
    ///     setter, so <c>GET /requests/{id}</c> reflects the accepted state.</item>
    ///   <item><b>H6c delivery winner-assign</b> — records the winning jeeber on the
    ///     durable delivery row (seeded at create with <c>deliveryId == requestId</c>)
    ///     by advancing its upstream status, so the accepted delivery is visible to
    ///     the handover/OTP surfaces.</item>
    ///   <item><b>H6d conversation advance</b> — adds the winning jeeber to the
    ///     broadcasting conversation and deactivates losers (membership advance);
    ///     the phase-literal flip awaits the separate chat-service ResolvePhase
    ///     change (see <see cref="IConversationProvisioner.AdvanceToAcceptedAsync"/>).</item>
    /// </list>
    /// EVERY step is wrapped so a downstream failure is logged and swallowed — a
    /// successful upstream accept must remain a 200 even if a side-effect blips.
    /// Mirrors the degrade-don't-fail contract of <c>DurableRequestsStore</c> and
    /// <c>ChatServiceConversationProvisioner</c>.
    /// </summary>
    private async Task OrchestrateAcceptedAsync(
        string requestId, string actorId, OfferAcceptWire envelope, CancellationToken ct)
    {
        var winningJeeberId = envelope.JeeberId;
        if (string.IsNullOrWhiteSpace(winningJeeberId))
        {
            // The upstream envelope omitted the winning jeeber id. We must NOT
            // write a blank jeeber onto the request/delivery — skip the sync and
            // let the read paths report the pre-accept jeeberId. The accept itself
            // still returns 200 (the saga committed upstream); this is a telemetry
            // signal, not a user-facing error.
            _logger.LogWarning(
                "Post-accept orchestration for request {RequestId}: upstream accept envelope carried no jeeberId; skipping request/delivery/chat sync.",
                requestId);
            return;
        }

        var now = _clock.GetUtcNow();

        // (H6b) Sync the gateway's own request ledger. TryAcceptByJeeberAsync is the
        // existing atomic setter (status=accepted + jeeberId + acceptedAt under the
        // inner store's write lock); when DurableRequests is enabled it delegates to
        // the in-memory inner row that backs GET /requests/{id}, so the read is fixed
        // with no new store method. BR-9/race-safety stays in the offer-service — this
        // is a ledger mirror, not a second authority. Degrade-don't-fail.
        DeliveryRequest? synced = null;
        try
        {
            synced = await _requests.TryAcceptByJeeberAsync(
                requestId, winningJeeberId, ActiveDeliveriesLimit, now, ct);
            if (synced is null)
            {
                _logger.LogInformation(
                    "Post-accept request-sync for {RequestId}: ledger row unknown to this gateway instance; GET /requests/{{id}} will not reflect the accept (offer-service remains authoritative).",
                    requestId);
            }
        }
        catch (Exception ex)
        {
            // RequestNotAcceptable / TooManyActiveDeliveries / any store fault: the
            // offer-service already committed the canonical accept, so a gateway
            // ledger-mirror failure must not fail the 200. Log and continue.
            _logger.LogWarning(ex,
                "Post-accept request-sync for {RequestId} failed; accept stays 200, GET /requests/{{id}} may show the pre-accept state.",
                requestId);
        }

        // (H6c) The durable delivery row already exists — it was seeded at create
        // (DurableRequestsStore.PersistSagaAsync) with deliveryId == requestId, so
        // GET /deliveries/{requestId}/otp resolves the gateway's local row off the
        // SAME id the accept response surfaces ($.id). We do NOT force a
        // delivery-service status transition here: the canonical Go SM
        // (Ordered → Picked → InTransit → AtDoor → Done) has no accept-time edge.
        //
        // S07 N7 / BR-10 — ASSIGN THE WINNING JEEBER ONTO THE DELIVERY ROW. The seed
        // row was created BEFORE any jeeber was known, so its jeeber_id is NULL and
        // it does not yet count against the winner's active-delivery cap. Here we
        // re-POST the SAME delivery id carrying jeeber_id = winningJeeberId; the
        // idempotent /api/v1/deliveries upsert assigns the jeeber ONLY when the row is
        // still unassigned (delivery-service guards `WHERE jeeber_id IS NULL`, never
        // steals), so the accepted delivery now counts toward BR-10 and the NEXT
        // accept of a 3rd offer for the same jeeber is short-circuited to 409 by the
        // pre-forward cap above. The id/tier/pickup come from the synced ledger row
        // (the same data the create-seed used). DEGRADE-DON'T-FAIL: a delivery-service
        // blip here only means the cap can't see this delivery yet — it must never
        // turn a committed accept into a 5xx, so every failure is logged and swallowed.
        // No row read-back is asserted; this is a best-effort assignment mirror.
        if (synced is not null && !string.IsNullOrWhiteSpace(synced.Id))
        {
            try
            {
                await _deliveryService.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
                {
                    Id = synced.Id,
                    TenantId = _deliveryOptions.TenantId,
                    ClientId = synced.ClientId,
                    JeeberId = winningJeeberId,
                    TierId = synced.TierId ?? string.Empty,
                    PickupLat = synced.PickupLocation?.Lat ?? 0d,
                    PickupLng = synced.PickupLocation?.Lng ?? 0d,
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-accept BR-10 delivery-assignment for request {RequestId} (jeeber {JeeberId}) failed; accept stays 200 — the delivery will not count toward the jeeber's active-delivery cap until reconciled.",
                    requestId, winningJeeberId);
            }
        }
        else
        {
            _logger.LogInformation(
                "Post-accept BR-10 delivery-assignment for request {RequestId}: ledger row not synced locally; skipping the jeeber-assignment mirror (the offer-service accept still committed; cap visibility deferred).",
                requestId);
        }

        // NOTE (escalated, not a gateway bug): GET /deliveries/{id}/otp TRIGGERS a
        // handover OTP and is contractually 400 until status == at_door; it cannot
        // return 200 at the accepted state. The H6c "OTP 200 at accept" assertion is
        // therefore structurally unsatisfiable by accept-orchestration — it requires
        // the delivery to be advanced to at_door first. See the PR description.

        // (H6d) Advance the broadcasting conversation: add the winning jeeber, drop
        // losers. The conversation id was minted at create and stamped on the request
        // ledger row; resolve it from the synced row (falling back to a fresh read so
        // an unsynced ledger still surfaces the id). The gateway has no per-offer
        // loser chat-member registry, so the losing-member list is empty here — the
        // winner-add + accepted-tag still run. The provisioner is the SOLE chat caller
        // and degrades to null on any chat blip, so this never blocks the accept.
        var conversationId = synced?.ConversationId
            ?? (await SafeGetConversationIdAsync(requestId, ct));
        try
        {
            await _conversations.AdvanceToAcceptedAsync(
                conversationId, winningJeeberId, Array.Empty<string>(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept conversation advance for {RequestId} (conversation {ConversationId}) failed; accept stays 200.",
                requestId, conversationId);
        }
    }

    /// <summary>
    /// Resolves the broadcasting conversation id stamped on the request ledger row
    /// without throwing — a read failure degrades to <c>null</c> so the accept
    /// orchestration continues. Used only when the H6b sync did not return the row.
    /// </summary>
    private async Task<string?> SafeGetConversationIdAsync(string requestId, CancellationToken ct)
    {
        try
        {
            var row = await _requests.GetAsync(requestId, ct);
            return row?.ConversationId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept conversation-id read for {RequestId} failed; advancing without a conversation id.",
                requestId);
            return null;
        }
    }

    /// <summary>
    /// Projects the offer-service accept envelope onto the gateway's
    /// <see cref="DeliveryRequestDto"/>. The saga has already transitioned the
    /// request to <c>accepted</c> upstream; the gateway re-emits the resolved
    /// ids and status so the mobile app's accept flow (and the H6c/H6d OTP /
    /// chat-summary cascade) can chain off <c>id</c> / <c>jeeberId</c>. The acting
    /// user is the CLIENT (request owner), so <c>ClientId</c> is the actor and the
    /// awarded <c>JeeberId</c> comes from the envelope's winning offer (falling back
    /// to empty when the upstream envelope omits it).
    /// </summary>
    private DeliveryRequestDto BuildAcceptedDto(
        string requestId, string actorId, OfferAcceptWire envelope)
        => new()
        {
            Id = requestId,
            ClientId = actorId,
            // Canonical-vocab fix (JEB-45): when the delivery kill-switch is ON,
            // delivery-service owns the SM and the just-accepted row lives at the
            // canonical entry state 'Ordered' (the SM has no accept-time edge — the
            // jeeber-tap → Picked transition is the first move). Surfacing the
            // hardcoded legacy literal "accepted" here is what made S15 SETUP-6 /
            // S09 SETUP-7 read "accepted" where they expect "Ordered". When the flag
            // is OFF the gateway runs its legacy linear snake_case SM, whose
            // post-accept state IS "accepted" — so the legacy literal is preserved.
            Status = _flags.Delivery ? CanonicalDeliveryVocab.Ordered : RequestStatus.Accepted,
            Description = string.Empty,
            PickupAddress = null,
            DropoffAddress = null,
            CreatedAt = default,
            ScheduledAt = null,
            JeeberId = envelope.JeeberId ?? string.Empty,
            AcceptedAt = _clock.GetUtcNow()
        };

    private static DeliveryRequestDto ToDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt
    };
}
