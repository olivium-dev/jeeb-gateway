using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
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
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<OffersController> _logger;

    public OffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IDualRoleService dualRole,
        TimeProvider clock,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<OffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _dualRole = dualRole;
        _clock = clock;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _flags = flags.Value;
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
    /// one delivery), a Jeeb product composition rule spanning request-creator
    /// identity rather than auction state.
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

        // BR-1 fast-fail (gateway-owned composition rule). The accepting CLIENT must
        // not also be a jeeber on this delivery. Cheap and avoids a pointless saga
        // round-trip; the offer-service does not know the Jeeb dual-role coupling.
        if (await _dualRole.WouldViolateSameDeliveryRuleAsync(actorId, requestId, ct))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot accept your own delivery request (BR-1).",
                Detail = "A user cannot act as both Client and Jeeber on the same delivery.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/same-delivery-role-violation"
            });
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
            Status = "accepted",
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
