using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// JEB-1431: V1 BFF slice for offer mutations.
///
/// <c>POST /v1/offers/{id}/accept</c> — accept an offer (close auction).
/// Delegates to the offer-service accept saga when the
/// <c>FeatureFlags:UseUpstream:Offer</c> flag is on; falls back to the
/// legacy in-memory store path when the flag is off. The caller is the
/// request-owning CLIENT, NOT the jeeber. State (ownership, single-winner
/// race safety, OTP mint, sibling rejection, chat-thread open) is owned
/// by the offer-service; the gateway forwards the actor and surfaces the
/// upstream outcome verbatim.
///
/// Coexists with the legacy (Obsolete) <see cref="JeebGateway.Controllers.OffersController"/>
/// — that surface is frozen per the GATEWAY-REMEDIATION-PLAN; all new work lands here.
/// </summary>
[ApiController]
public sealed class JeebOffersController : ControllerBase
{
    /// <summary>BR-10: per-jeeber maximum of concurrent active deliveries.</summary>
    private const int ActiveDeliveriesLimit = 2;

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly IOfferServiceClient _offerService;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IDeliveryServiceClient _delivery;
    private readonly UpstreamFeatureFlags _flags;
    private readonly DeliveryClientOptions _deliveryOptions;
    private readonly ILogger<JeebOffersController> _logger;

    public JeebOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IDeliveryServiceClient delivery,
        IOptions<UpstreamFeatureFlags> flags,
        IOptions<DeliveryClientOptions> deliveryOptions,
        ILogger<JeebOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _delivery = delivery;
        _flags = flags.Value;
        _deliveryOptions = deliveryOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/offers/{id}/accept — accept an offer, closing the auction.
    ///
    /// The caller is the request-owning CLIENT awarding the delivery to one
    /// jeeber's bid.
    ///
    /// Upstream path (<c>FeatureFlags:UseUpstream:Offer = true</c>): the gateway
    /// resolves <c>offerId → requestId</c> via the in-process offer routing index,
    /// then forwards to the offer-service accept saga which owns OTP mint,
    /// chat-thread open, sibling rejection, and SELECT FOR UPDATE race-safety.
    /// The upstream HTTP status is surfaced verbatim.
    ///
    /// In-memory path (flag off): the local accept guard runs BR-10 and ownership
    /// checks before committing. This path is legacy/test-only.
    /// </summary>
    [HttpPost("v1/offers/{id}/accept")]
    // ADR-005 L2 / S07: offer.accept {client} — the CLIENT accepts the bid, not the jeeber.
    [RequireCapability(Capabilities.OfferAccept)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(
        string id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem))
            return problem;

        if (_flags.Offer)
            return await AcceptUpstreamAsync(id, actorId, idempotencyKey, ct);

        return await AcceptInMemoryAsync(id, actorId, ct);
    }

    // -----------------------------------------------------------------------
    // Upstream (offer-service) accept path
    // -----------------------------------------------------------------------

    private async Task<IActionResult> AcceptUpstreamAsync(
        string offerId,
        string actorId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        // Resolve offerId → requestId via the in-process routing index learned at
        // offer-submission time (populated by RequestOffersController.Submit).
        var requestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (requestId is null)
            return NotFound();

        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : idempotencyKey;

        var result = await _offerService.AcceptWithStatusAsync(actorId, requestId, offerId, key, ct);

        return result.Status switch
        {
            OfferAcceptStatus.Accepted => await BuildAcceptedResponseAsync(requestId, result, ct),
            OfferAcceptStatus.NotOwner => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Only the request owner may accept an offer.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/offer-not-owned"
            }),
            OfferAcceptStatus.NotFound => NotFound(),
            OfferAcceptStatus.Expired => Conflict(new ProblemDetails
            {
                Title = "Request expired before acceptance.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-expired",
                Extensions = { ["upstreamCode"] = result.UpstreamCode }
            }),
            OfferAcceptStatus.Conflict => Conflict(new ProblemDetails
            {
                Title = "Offer or request is no longer acceptable.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-acceptable",
                Extensions = { ["upstreamCode"] = result.UpstreamCode }
            }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Unexpected upstream accept outcome.",
                Status = StatusCodes.Status502BadGateway
            })
        };
    }

    private async Task<IActionResult> BuildAcceptedResponseAsync(
        string requestId,
        OfferAcceptResult result,
        CancellationToken ct)
    {
        var req = await _requests.GetAsync(requestId, ct);

        // BUG-5 (delivery-aggregate-not-created-on-accept): the offer-service accept
        // saga commits the auction outcome (request -> accepted, winner assigned,
        // siblings rejected) and the jeeber-side list projection reflects it — but
        // NOTHING here ever materialised the canonical delivery aggregate. With
        // UseUpstream:Delivery forced on, GET /v1/deliveries/{requestId} reads the
        // delivery-service row via GetCanonicalDeliveryAsync and 404s, so the
        // "Manage delivery" / "Live tracking" screens and the handover-OTP leg are
        // unreachable. The legacy OffersController.OrchestrateAcceptedAsync already
        // performs this delivery-row materialisation post-accept; the V1 surface
        // (where all new work lands) never carried it over. We close the gap HERE,
        // by idempotently upserting the delivery aggregate with the winning jeeber
        // assigned, so the by-id read resolves immediately after accept.
        //
        // DEGRADE-DON'T-FAIL: the offer-service accept has already committed, so a
        // delivery-service blip on this mirror must NEVER turn the 200 into a 5xx.
        // Every failure is logged and swallowed.
        await TryMaterializeDeliveryAggregateAsync(requestId, req, result, ct);

        if (req is not null)
            return Ok(ToRequestDto(req));

        // Request not in local store (delivery-service is the SoT).
        // Return a minimal acknowledgement so the client knows acceptance succeeded.
        return Ok(new
        {
            requestId,
            acceptedOfferId = result.Envelope?.AcceptedOfferId,
            jeeberId = result.Envelope?.JeeberId,
            status = "accepted"
        });
    }

    /// <summary>
    /// BUG-5: materialise / assign the canonical delivery aggregate in
    /// delivery-service immediately after the offer-service accept saga commits, so
    /// <c>GET /v1/deliveries/{requestId}</c> (canonical read-through when
    /// <c>UseUpstream:Delivery</c> is on) resolves a row instead of 404-ing.
    ///
    /// Re-POSTs the SAME id to the idempotent create-row endpoint
    /// (<c>POST /api/v1/deliveries</c>, <c>ON CONFLICT (id) DO NOTHING</c>) carrying
    /// the winning <c>jeeber_id</c>. delivery-service assigns the jeeber ONLY when
    /// the row is still unassigned (<c>WHERE jeeber_id IS NULL</c>, never steals), so
    /// this is safe whether the row was seeded at request-create time (then this is a
    /// late winner-assignment) or never seeded (then this creates it). Mirrors the
    /// proven H6c step of <see cref="JeebGateway.Controllers.OffersController"/>.
    ///
    /// The winner is taken from the upstream accept envelope (the authority); the
    /// id/tier/pickup/client come from the gateway's own request row. When the row
    /// is unknown locally or lacks tier/pickup we skip rather than seed a malformed
    /// row — the offer-service remains the source of truth and the next read can be
    /// reconciled. ALWAYS degrade-don't-fail: a fault here never fails the accept.
    /// </summary>
    private async Task TryMaterializeDeliveryAggregateAsync(
        string requestId,
        DeliveryRequest? req,
        OfferAcceptResult result,
        CancellationToken ct)
    {
        // The authoritative winner is the offer-service envelope's jeeber; fall back
        // to the locally-recorded winner if the envelope omitted it.
        var winnerJeeberId = result.Envelope?.JeeberId;
        if (string.IsNullOrWhiteSpace(winnerJeeberId))
            winnerJeeberId = req?.JeeberId;

        if (string.IsNullOrWhiteSpace(winnerJeeberId))
        {
            _logger.LogWarning(
                "BUG-5: accept for request {RequestId} committed but no winning jeeberId is known (envelope + ledger both empty); skipping delivery-aggregate materialisation. GET /v1/deliveries/{RequestId} may 404 until reconciled.",
                requestId, requestId);
            return;
        }

        // Need tier + pickup to seed/assign the canonical row; these live on the
        // gateway's request row (seeded there at create). Without them we cannot
        // supply the delivery-service create-row contract, so skip rather than POST
        // a malformed aggregate. The offer-service accept still stands (200).
        if (req is null || req.PickupLocation is null || string.IsNullOrWhiteSpace(req.TierId))
        {
            _logger.LogWarning(
                "BUG-5: accept for request {RequestId} (jeeber {JeeberId}) committed but the local request row is unknown/missing tier or pickup; skipping delivery-aggregate materialisation (delivery-service remains SoT).",
                requestId, winnerJeeberId);
            return;
        }

        try
        {
            await _delivery.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
            {
                Id = req.Id,
                TenantId = _deliveryOptions.TenantId,
                ClientId = req.ClientId,
                JeeberId = winnerJeeberId,
                TierId = req.TierId!,
                PickupLat = req.PickupLocation.Lat,
                PickupLng = req.PickupLocation.Lng,
            }, ct);

            _logger.LogInformation(
                "BUG-5: materialised delivery aggregate for request {RequestId} with winning jeeber {JeeberId} (idempotent upsert); GET /v1/deliveries/{RequestId} now resolves.",
                requestId, winnerJeeberId, requestId);
        }
        catch (Exception ex)
        {
            // delivery-service unreachable / non-2xx (and non-idempotent-409): the
            // accept already committed upstream, so we log and swallow. The delivery
            // will not be readable by-id until reconciled, but the 200 is preserved.
            _logger.LogWarning(ex,
                "BUG-5: delivery-aggregate materialisation for request {RequestId} (jeeber {JeeberId}) failed; accept stays 200 — GET /v1/deliveries/{RequestId} may 404 until reconciled.",
                requestId, winnerJeeberId, requestId);
        }
    }

    // -----------------------------------------------------------------------
    // In-memory accept path (legacy / test-only; flag off)
    // -----------------------------------------------------------------------

    private async Task<IActionResult> AcceptInMemoryAsync(string offerId, string actorId, CancellationToken ct)
    {
        var offer = await _offers.GetAsync(offerId, ct);
        if (offer is null)
            return NotFound();

        // Ownership guard: the acceptor must be the request's owning client.
        var request = await _requests.GetAsync(offer.RequestId, ct);
        if (request is not null
            && !string.Equals(request.ClientId, actorId, StringComparison.Ordinal))
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
            // SM-2 / ACC-02 re-accept → 409 already_accepted with the winner.
            var supersedeOutcome = await _offers.AcceptWithSupersedeAsync(offerId, DateTimeOffset.UtcNow, ct);
            if (supersedeOutcome.Status == AcceptOfferStatus.AlreadyAccepted)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Offer already accepted.",
                    Detail = $"The auction for this request is closed; the winning Jeeber is {supersedeOutcome.WinnerJeeberId}.",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/already-accepted",
                    Extensions = { ["winnerJeeberId"] = supersedeOutcome.WinnerJeeberId }
                });
            }

            return Conflict(new ProblemDetails
            {
                Title = $"Offer is no longer pending (current={offer.Status}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-pending"
            });
        }

        DeliveryRequest? accepted;
        try
        {
            accepted = await _requests.TryAcceptByJeeberAsync(
                offer.RequestId,
                offer.JeeberId,
                ActiveDeliveriesLimit,
                DateTimeOffset.UtcNow,
                ct);
        }
        catch (TooManyActiveDeliveriesException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Maximum 2 active deliveries. Complete a delivery before accepting another.",
                Detail = $"Jeeber has {ex.ActiveCount} active deliveries (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-deliveries"
            });
        }
        catch (RequestNotAcceptableException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = $"Request is no longer in a pre-acceptance state (current={ex.CurrentStatus}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-not-acceptable"
            });
        }

        if (accepted is null)
            return NotFound();

        // ACC-02: supersede every competing bid on the same request.
        await _offers.AcceptWithSupersedeAsync(offerId, DateTimeOffset.UtcNow, ct);

        return Ok(ToRequestDto(accepted));
    }

    private static DeliveryRequestDto ToRequestDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        Transcription = r.Transcription,
        AudioUrl = r.AudioUrl,
        Photos = r.Photos,
        TierId = r.TierId,
        PickupLocation = r.PickupLocation,
        DropoffLocation = r.DropoffLocation,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        RecipientPhone = r.RecipientPhone,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt,
        ConversationId = r.ConversationId,
        GpsTrackingActive = r.GpsTrackingActive,
        OtpAttemptCount = r.OtpAttemptCount,
        OtpLockedAt = r.OtpLockedAt,
        ClientUnreachableAt = r.ClientUnreachableAt,
        OtpEscalationId = r.OtpEscalationId,
    };
}
