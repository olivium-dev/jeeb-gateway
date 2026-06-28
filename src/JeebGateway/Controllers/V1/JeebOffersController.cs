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
    private readonly IDeliveryServiceClient _deliveryService;
    private readonly UpstreamFeatureFlags _flags;
    private readonly DeliveryClientOptions _deliveryOptions;
    private readonly ILogger<JeebOffersController> _logger;

    public JeebOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IDeliveryServiceClient deliveryService,
        IOptions<UpstreamFeatureFlags> flags,
        IOptions<DeliveryClientOptions> deliveryOptions,
        ILogger<JeebOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _deliveryService = deliveryService;
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

        // S07 N7 / BR-10 — DELIVERED-leg sync. The offer-service accept saga owns the
        // single-winner transition but NOT the delivery row (org no-coupling law:
        // offer/delivery/chat services never call each other), so the gateway BFF is
        // the composer that assigns the winning jeeber onto the durable delivery row.
        // The legacy (Obsolete) OffersController did this; this thin V1 slice (the
        // route mobile actually calls) must do it too, or the accepted delivery never
        // counts against the jeeber's active-delivery cap and the next accept of a 3rd
        // offer is not short-circuited. Mirrors OffersController.OrchestrateAcceptedAsync
        // (H6c). DEGRADE-DON'T-FAIL: the saga already committed upstream, so any
        // delivery-service blip here is logged and swallowed — the accept stays 200.
        await SyncDeliveryLegAsync(req, result.Envelope?.JeeberId, ct);

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
    /// S07 N7 / BR-10 — best-effort post-accept DELIVERED-leg assignment. After the
    /// offer-service accept saga commits the single-winner transition, the gateway
    /// (the SOLE cross-service composer) re-POSTs the durable delivery row carrying
    /// <c>jeeber_id = winningJeeberId</c>. delivery-service upserts the jeeber ONLY
    /// when the row is still unassigned (<c>WHERE jeeber_id IS NULL</c>, never steals),
    /// so this is idempotent: it composes cleanly with the create-time matching mirror
    /// and a retried accept. The row was seeded at request-create time
    /// (<see cref="JeebGateway.Requests.DurableRequestsStore"/>) with
    /// <c>deliveryId == requestId</c>, so the same id is reused here.
    ///
    /// DEGRADE-DON'T-FAIL: the saga already committed the canonical accept upstream, so
    /// every failure path (winner unknown, request not locally synced, missing
    /// tier/pickup, delivery-service fault, cancellation) is logged and swallowed — it
    /// must NEVER convert a successful accept into a 5xx. No read-back is asserted; this
    /// is a best-effort assignment mirror, exactly matching
    /// <see cref="JeebGateway.Controllers.OffersController"/>'s H6c step.
    /// </summary>
    private async Task SyncDeliveryLegAsync(
        DeliveryRequest? request, string? winningJeeberId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(winningJeeberId))
        {
            // The upstream envelope omitted the winning jeeber id — never write a blank
            // jeeber onto the delivery row. Telemetry signal, not a user-facing error.
            _logger.LogWarning(
                "Post-accept delivery-leg sync: upstream accept envelope carried no jeeberId; "
                + "skipping the delivery-row assignment (accept stays 200).");
            return;
        }

        // The matching-resolve columns (tier + pickup) are required by the create-row
        // contract; without a locally-synced request row carrying them there is nothing
        // to seed. delivery-service remains the authority — the cap visibility is simply
        // deferred until a path with the full row reconciles it.
        if (request is null
            || request.PickupLocation is null
            || string.IsNullOrWhiteSpace(request.TierId)
            || string.IsNullOrWhiteSpace(request.Id))
        {
            _logger.LogInformation(
                "Post-accept delivery-leg sync for jeeber {JeeberId}: request row not locally "
                + "available with tier/pickup; skipping the assignment mirror (accept stays 200).",
                winningJeeberId);
            return;
        }

        try
        {
            await _deliveryService.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
            {
                Id = request.Id,
                TenantId = _deliveryOptions.TenantId,
                ClientId = request.ClientId,
                JeeberId = winningJeeberId,
                TierId = request.TierId!,
                PickupLat = request.PickupLocation.Lat,
                PickupLng = request.PickupLocation.Lng,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate nothing; the accept response is already shaped.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept delivery-leg sync for request {RequestId} (jeeber {JeeberId}) failed; "
                + "accept stays 200 — the delivery will not count toward the jeeber's active-delivery "
                + "cap until reconciled.",
                request.Id, winningJeeberId);
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

        await _offers.AcceptAsync(offerId, DateTimeOffset.UtcNow, ct);

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
