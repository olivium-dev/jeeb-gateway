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
/// Jeeber-facing offer acceptance endpoint. Accepting an offer is the
/// moment a Jeeber commits to a specific request — at that point the
/// gateway enforces BR-10 (T-backend-039): a Jeeber may have at most
/// <see cref="ActiveDeliveriesLimit"/> active deliveries
/// (statuses <c>accepted</c>, <c>picked_up</c>, <c>heading_off</c>).
///
/// 409 outcomes:
///   * <c>too-many-active-deliveries</c> — BR-10 cap hit.
///   * <c>request-not-acceptable</c> — request moved out of
///     pre-acceptance (another Jeeber won the race, expired, cancelled).
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
    // ADR-005 L2 / JEB-1509: this route declares the distinct offer.accept {jeeber} capability. The
    // body is unambiguously the JEEBER committing to a delivery the offer was extended to
    // (offer.JeeberId == caller; BR-10 jeeber active-deliveries cap; BR-1 same-delivery — all STATE,
    // stay below). Pre-cleanup this carried offer.submit {jeeber} as a behaviour-preserving stand-in
    // because the authoritative map keyed the (dead) offer.accept -> {client}. JEB-1509 repoints
    // offer.accept -> {jeeber} and points this route at it: a PURE NO-OP RENAME (allowed user type
    // stays {jeeber}) that makes the route's intent (accept, not submit) explicit. STATE
    // (ownership/BR-1/BR-10/status) stays in the offer/delivery service.
    [RequireCapability(Capabilities.OfferAccept)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(string offerId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var problem)) return problem;

        // Thin-BFF wire: when the offer ledger is the real offer-service, the
        // gateway does NOT re-run the auction. It forwards to the offer-service
        // accept saga (which owns OTP mint, chat-thread open, sibling rejection,
        // request transition, and SELECT FOR UPDATE + optimistic_lock
        // race-safety) and re-emits the upstream status verbatim. The in-memory
        // Get/Accept seam below (which throws NotSupportedException upstream) is
        // never touched on this path.
        if (_flags.Offer)
        {
            return await AcceptViaUpstreamAsync(offerId, jeeberId, ct);
        }

        var offer = await _offers.GetAsync(offerId, ct);
        if (offer is null) return NotFound();

        // The offer is bound to a specific Jeeber at creation. Another
        // Jeeber trying to claim it is forbidden, not "not found" — the
        // distinction matters so the mobile app can show the correct
        // banner and ops can spot the case in audit logs.
        if (!string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "This offer was extended to a different Jeeber.",
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

        // BR-1 (T-backend-041): a user cannot act as both Client and Jeeber
        // on the same delivery. This check runs before the heavy atomic
        // accept so we fail fast without holding the store write lock.
        if (await _dualRole.WouldViolateSameDeliveryRuleAsync(jeeberId, offer.RequestId, ct))
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
            accepted = await _requests.TryAcceptByJeeberAsync(
                offer.RequestId,
                jeeberId,
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
    /// Upstream accept path (FeatureFlags:UseUpstream:Offer = true). Resolves the
    /// offer's request via the BFF routing index, forwards to the offer-service
    /// accept saga, and re-emits the upstream status verbatim. No auction rule is
    /// recomputed here — the offer-service owns every negative (403/410/409/404)
    /// and the race-safe single-winner guarantee. The one gateway-owned
    /// pre-forward check is BR-1 (same user cannot be both Client and Jeeber on
    /// one delivery), a Jeeb product composition rule spanning request-creator
    /// identity rather than auction state.
    /// </summary>
    private async Task<IActionResult> AcceptViaUpstreamAsync(
        string offerId, string jeeberId, CancellationToken ct)
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

        // BR-1 fast-fail (gateway-owned composition rule). Cheap and avoids a
        // pointless saga round-trip; the offer-service does not know the Jeeb
        // dual-role identity coupling.
        if (await _dualRole.WouldViolateSameDeliveryRuleAsync(jeeberId, requestId, ct))
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
        // dedupe accept retries. Deterministic per (jeeber, offer) so a client
        // retry of the SAME accept replays rather than double-applying.
        var idempotencyKey = $"accept-{jeeberId}-{offerId}";

        var result = await _offerService.AcceptWithStatusAsync(
            jeeberId, requestId, offerId, idempotencyKey, ct);

        switch (result.Status)
        {
            case OfferAcceptStatus.Accepted:
                return Ok(BuildAcceptedDto(requestId, jeeberId, result.Envelope!));

            case OfferAcceptStatus.NotOwner:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "This offer was extended to a different Jeeber.",
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
    /// chat-summary cascade) can chain off <c>id</c> / <c>jeeberId</c>.
    /// </summary>
    private DeliveryRequestDto BuildAcceptedDto(
        string requestId, string jeeberId, OfferAcceptWire envelope)
        => new()
        {
            Id = requestId,
            ClientId = string.Empty, // owned upstream; not surfaced on the accept envelope
            Status = "accepted",
            Description = string.Empty,
            PickupAddress = null,
            DropoffAddress = null,
            CreatedAt = default,
            ScheduledAt = null,
            JeeberId = jeeberId,
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
