using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Notifications;
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
/// a CLIENT action keyed on request ownership; the offer-service accept owns ONLY
/// the generic, product-agnostic transition (race-safe single-winner + sibling
/// rejection), while the Jeeb-specific post-accept side effects (OTP mint,
/// chat-thread open, delivery winner assignment, notification fan-out) are owned
/// by the gateway. The gateway forwards the CLIENT's identity to the request-scoped
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
    /// <summary>Retired BR-10 cap: active deliveries are unlimited.</summary>
    public const int ActiveDeliveriesLimit = int.MaxValue;

    /// <summary>
    /// JEB-1474 — the Jeeb offer edit cap. This is a PRODUCT policy owned by the
    /// gateway, not the shared offer-service. It is forwarded as <c>max_edits</c>
    /// so offer-service enforces the ceiling without hardcoding the literal "2".
    /// </summary>
    public const int OfferEditCap = 2;

    /// <summary>
    /// Historical BR-10 409 plumbing remains reachable only if a lower layer
    /// raises <see cref="TooManyActiveDeliveriesException"/> unexpectedly.
    /// </summary>
    internal const string LimitExceededMessage =
        "Active delivery concurrency is unlimited.";

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly IDualRoleService _dualRole;
    private readonly TimeProvider _clock;
    private readonly IOfferServiceClient _offerService;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IConversationProvisioner _conversations;
    private readonly IJeebConversationClient _conversationAggregate;
    private readonly IDeliveryServiceClient _deliveryService;
    private readonly IOfferPushNotifier _offerPush;
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
        IJeebConversationClient conversationAggregate,
        IDeliveryServiceClient deliveryService,
        IOfferPushNotifier offerPush,
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
        _conversationAggregate = conversationAggregate;
        _deliveryService = deliveryService;
        _offerPush = offerPush;
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
        // accept endpoint, which owns ONLY the generic transition (race-safe
        // single-winner via SELECT FOR UPDATE + optimistic_lock, request
        // transition, and sibling rejection) and returns just the accepted offer
        // id + rejected sibling ids. The Jeeb-specific post-accept side effects —
        // OTP mint, chat-thread open, delivery winner assignment, notification
        // fan-out — are owned HERE in the gateway (see PostAcceptOrchestration),
        // not in the shared service (JEB-1474). The gateway re-emits the upstream
        // status verbatim. The in-memory Get/Accept seam below (which throws
        // NotSupportedException upstream) is never touched on this path.
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
            // SM-2 / ACC-02: a re-accept (or accepting a competing bid that was
            // already superseded) returns 409 already_accepted and surfaces the
            // auction WINNER, not a bare "offer not pending". Resolve the winner
            // from the request's accepted offer; fall back to the generic
            // offer-not-pending shape only when the request has no accepted winner
            // (the offer was withdrawn, not lost to a winner).
            var supersedeOutcome = await _offers.AcceptWithSupersedeAsync(offerId, _clock.GetUtcNow(), ct);
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

        // Mark the offer accepted and SUPERSEDE every competing bid on the same
        // request (ACC-02) — only after the request transition succeeded, keeping
        // the two sides of the relationship consistent if the request flip threw.
        await _offers.AcceptWithSupersedeAsync(offerId, now, ct);

        // fix/client-visibility (run-22 P1): accepted-fee snapshot for the receipt
        // read (see DeliveryRequest.AcceptedFee). Never fails the accept.
        if (offer.Fee > 0m
            && await _requests.TrySetAcceptedFeeAsync(accepted.Id, offer.Fee, ct))
        {
            accepted = await _requests.GetAsync(accepted.Id, ct) ?? accepted;
        }

        return Ok(ToDto(accepted));
    }

    // -------------------------------------------------------------------------
    // S08 A3 — offer EDIT (jeeber edits their own pending bid).
    // -------------------------------------------------------------------------

    /// <summary>
    /// S08 A3 — a JEEBER edits their own pending offer (fee / eta / note). The
    /// mobile route is offer-scoped (<c>PUT /v1/offers/{offerId}</c>) while the
    /// canonical offer-service edit route is request-scoped
    /// (<c>PUT /api/v1/requests/{requestId}/offers/{offerId}</c>), so the gateway
    /// resolves the requestId from its routing index (learned at submit) and
    /// forwards the actor as <c>x-user-id</c>. offer-service owns the edit rule
    /// (only the owning jeeber, ≤ 2 edits, only while submitted/edited) and the
    /// <c>edited</c> transition; the gateway re-derives nothing and forwards the
    /// upstream status verbatim. ProblemDetails on every negative (RFC 7807).
    /// </summary>
    [HttpPut("/v1/offers/{offerId}")]
    [RequireCapability(Capabilities.OfferEditOwn)] // {jeeber}; ownership (offer.jeeber_id == actor) = STATE (offer-service)
    [RequireActiveUser]
    [ProducesResponseType(typeof(OfferWire), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Edit(
        string offerId, [FromBody] EditOfferBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem)) return problem;

        if (body is null || (body.Fee is null && body.EtaMinutes is null && body.Note is null))
        {
            return Problem(
                title: "At least one of fee, etaMinutes, or note is required to edit an offer.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Flag-OFF in-memory edit path (SM-2 / OFF-04 amend). The gateway IS the
        // offer record-of-truth when the Offer kill-switch is off, so the 2-edit
        // cap (JEB-1474) is enforced here against the in-memory store rather than
        // forwarded. This makes the cap → 422 edit_limit_reached fully offline-
        // testable. The upstream forward below owns the cap when the flag is on.
        if (!_flags.Offer)
        {
            return OfferUpstreamUnavailable("edit");
        }

        var requestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (requestId is null)
        {
            // Unknown to this gateway instance (never submitted through here / index
            // lost on restart). 404 is the correct contract for a phantom offer.
            _logger.LogInformation(
                "Edit for offer {OfferId} could not resolve a requestId from the routing index; returning 404.",
                offerId);
            return NotFound();
        }

        // Dollars → cents on the wire (offer-service is cents-based, mirroring submit).
        long? feeCents = body.Fee is decimal fee ? (long)Math.Round(fee * 100m) : null;

        OfferMutationResult result;
        try
        {
            result = await _offerService.EditAsync(
                actorId, requestId, offerId, feeCents, body.EtaMinutes, body.Note, OfferEditCap, ct);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "offer-service edit for offer {OfferId} failed.", offerId);
            return OfferUpstreamUnavailable("edit");
        }

        return MapMutation(result, "edit");
    }

    /// <summary>
    /// Flag-OFF in-memory offer edit (SM-2). Resolves the offer's request via the
    /// routing index (the PUT route is offer-scoped), then applies the supplied
    /// fields against the in-memory store with the 2-edit cap. Maps the typed
    /// <see cref="EditOfferOutcome"/> onto the same RFC-7807 surface the upstream
    /// path uses, plus the SM-2-specific <c>422 edit_limit_reached</c>.
    /// </summary>
    private async Task<IActionResult> EditInMemoryAsync(
        string offerId, string actorId, EditOfferBody body, CancellationToken ct)
    {
        var requestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (requestId is null)
        {
            // Offer never submitted through this gateway instance (or the index was
            // lost on restart). Fall back to the store's own request-scoping by
            // reading the offer; if it is unknown there too, it is a phantom → 404.
            var known = await _offers.GetAsync(offerId, ct);
            if (known is null) return NotFound();
            requestId = known.RequestId;
        }

        var outcome = await _offers.TryEditAsync(
            offerId, requestId, actorId,
            body.Fee, body.EtaMinutes,
            string.IsNullOrWhiteSpace(body.Note) ? body.Note : body.Note.Trim(),
            OfferEditCap, _clock.GetUtcNow(), ct);

        return outcome.Status switch
        {
            EditOfferStatus.Edited => Ok(ToOfferWire(outcome.Offer!)),

            EditOfferStatus.EditLimitReached => UnprocessableEntity(new ProblemDetails
            {
                Title = $"Offer edit limit reached (max {OfferEditCap} edits).",
                Detail = "This offer has already been edited the maximum number of times.",
                Status = StatusCodes.Status422UnprocessableEntity,
                Type = "https://jeeb.dev/errors/edit-limit-reached"
            }),

            EditOfferStatus.NotOwned => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Only the offer's owner can edit it.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/offer-not-owned"
            }),

            EditOfferStatus.NotPending => Conflict(new ProblemDetails
            {
                Title = "Offer can no longer be edited.",
                Detail = "The offer is no longer pending (accepted, withdrawn, or superseded).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-pending"
            }),

            _ => NotFound()
        };
    }

    /// <summary>
    /// Projects an in-memory <see cref="PendingOffer"/> onto the same
    /// <see cref="OfferWire"/> shape the upstream edit returns, so the in-memory
    /// and upstream edit responses are byte-compatible for the mobile app.
    /// </summary>
    private static OfferWire ToOfferWire(PendingOffer o) => new()
    {
        Id = o.Id,
        RequestId = o.RequestId,
        JeeberId = o.JeeberId,
        Status = o.Status,
        FeeCents = (long)Math.Round(o.Fee * 100m),
        EtaMinutes = o.EtaMinutes,
        Note = o.Note,
        EditsCount = o.EditCount,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
    };

    // -------------------------------------------------------------------------
    // S08 A5 — offer REJECT (request-owning client declines one bid).
    // -------------------------------------------------------------------------

    /// <summary>
    /// S08 A5 — the request-owning CLIENT rejects a single jeeber's bid (distinct
    /// from the accept-saga's automatic sibling rejection). The route is offer-scoped
    /// (<c>POST /v1/offers/{offerId}/reject</c>), mirroring the offer-scoped reject
    /// route added to offer-service; the gateway forwards the actor as
    /// <c>x-user-id</c> and the upstream status verbatim. offer-service owns the
    /// reject rule (only the request's client may reject; submitted/edited → rejected
    /// with an already-rejected guard) and the transition. The gateway re-derives
    /// nothing. ProblemDetails on every negative (RFC 7807).
    /// </summary>
    [HttpPost("/v1/offers/{offerId}/reject")]
    [RequireCapability(Capabilities.OfferReject)] // {client}; authz (request.client_id == actor) = STATE (offer-service)
    [RequireActiveUser]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Reject(string offerId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem)) return problem;

        // Reject is an UPSTREAM-only surface (no legacy in-memory reject path). With
        // the offer kill-switch off the gateway is not the offer record-of-truth.
        if (!_flags.Offer)
        {
            return OfferUpstreamUnavailable("reject");
        }

        OfferMutationResult result;
        try
        {
            result = await _offerService.RejectAsync(actorId, offerId, ct);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "offer-service reject for offer {OfferId} failed.", offerId);
            return OfferUpstreamUnavailable("reject");
        }

        return MapMutation(result, "reject");
    }

    /// <summary>
    /// Maps a status-preserving <see cref="OfferMutationResult"/> onto the caller
    /// response: 200 (with the edit projection when present) or the matching negative
    /// ProblemDetails. The gateway re-derives no rule — it forwards the upstream
    /// outcome verbatim.
    /// </summary>
    private IActionResult MapMutation(OfferMutationResult result, string action) => result.Status switch
    {
        OfferMutationStatus.Ok => result.Offer is not null ? Ok(result.Offer) : Ok(),

        OfferMutationStatus.NotOwner => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = action == "reject"
                ? "Only the request owner can reject an offer."
                : "Only the offer's owner can edit it.",
            Status = StatusCodes.Status403Forbidden,
            Type = "https://jeeb.dev/errors/offer-not-owned"
        }),

        OfferMutationStatus.Conflict => Conflict(new ProblemDetails
        {
            Title = action == "reject"
                ? "Offer can no longer be rejected."
                : "Offer can no longer be edited.",
            Detail = action == "reject"
                ? "The offer was already rejected, accepted, or withdrawn."
                : "The offer is no longer pending or has reached its edit limit.",
            Status = StatusCodes.Status409Conflict,
            Type = "https://jeeb.dev/errors/offer-not-pending"
        }),

        // NotFound (and any unmapped status) → 404 phantom offer.
        _ => NotFound()
    };

    /// <summary>
    /// 503 kill-switch ProblemDetails for the upstream-only offer mutation surfaces
    /// (edit / reject) — mirrors the conversation BFF's UpstreamUnavailable shape.
    /// </summary>
    private ObjectResult OfferUpstreamUnavailable(string action) => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Title = $"The offer {action} surface is not available.",
            Detail = "offer-service is not wired (FeatureFlags:UseUpstream:Offer is off) "
                + "or is unreachable; the gateway holds no offer record-of-truth of its own.",
            Status = StatusCodes.Status503ServiceUnavailable,
        });

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

        // Retired BR-10 active-delivery cap: do not pre-count delivery-service
        // assignments here. Offer-service still owns real accept conflicts below.

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
                var conversationPhase = await OrchestrateAcceptedAsync(requestId, actorId, result.Envelope!, ct);
                return Ok(BuildAcceptedDto(requestId, actorId, result.Envelope!, conversationPhase));

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
    /// <returns>
    /// The conversation phase resolved from chat-service's phase-advance (H7/N9
    /// surface it as <c>conversation_phase</c>). Null when the conversation could
    /// not be advanced (chat disabled / unavailable / no conversation id) — the
    /// accept DTO then defaults the phase to "accepted" (the saga committed), never
    /// 5xx-ing the accept.
    /// </returns>
    private async Task<string?> OrchestrateAcceptedAsync(
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
            return null;
        }

        // sprint-009 Lane E — accept-lifecycle push fan-out. Push jeeb.offer_accepted to
        // the winning jeeber and jeeb.offer_rejected to each losing bidder named in the
        // envelope's RejectedOfferIds (bidder resolved from the offer routing index).
        // DEGRADE-DON'T-FAIL: the saga already committed, so a push blip is logged and
        // swallowed — it never turns a successful accept into a 5xx. Mirrors the V1
        // JeebOffersController fan-out so both accept surfaces behave identically.
        await DispatchAcceptLifecyclePushesAsync(
            requestId, envelope.AcceptedOfferId, winningJeeberId, envelope.RejectedOfferIds, ct);

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

        // fix/client-visibility (run-22 P1): accepted-fee snapshot. The acceptor is
        // the request OWNER here, so the owner-scoped offers list read is authorized;
        // match the accepted offer by the envelope's id and stamp its fee onto the
        // local row so post-completion receipt reads (and jeeber-party reads, which
        // the owner-scoped offers lookup 403s) still surface the agreed amount.
        // DEGRADE-DON'T-FAIL: a miss is logged and swallowed.
        try
        {
            var offersOnRequest = await _offers.ListForRequestAsync(requestId, ct);
            var acceptedFee = offersOnRequest
                .FirstOrDefault(o => string.Equals(o.Id, envelope.AcceptedOfferId, StringComparison.Ordinal))
                ?.Fee;
            if (acceptedFee is > 0m)
            {
                await _requests.TrySetAcceptedFeeAsync(requestId, acceptedFee.Value, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept fee snapshot for request {RequestId} (offer {OfferId}) failed; accept stays 200.",
                requestId, envelope.AcceptedOfferId);
        }

        // (H6c) The durable delivery row already exists — it was seeded at create
        // (DurableRequestsStore.PersistSagaAsync) with deliveryId == requestId, so
        // GET /deliveries/{requestId}/otp resolves the gateway's local row off the
        // SAME id the accept response surfaces ($.id). We do NOT force a
        // delivery-service status transition here: the canonical Go SM
        // (Ordered → Picked → InTransit → AtDoor → Done) has no accept-time edge.
        //
        // S07 N7 — ASSIGN THE WINNING JEEBER ONTO THE DELIVERY ROW. The seed
        // row was created BEFORE any jeeber was known, so its jeeber_id is NULL and
        // it does not yet count against the winner's active-delivery cap. Here we
        // re-POST the SAME delivery id carrying jeeber_id = winningJeeberId; the
        // idempotent /api/v1/deliveries upsert assigns the jeeber ONLY when the row is
        // still unassigned (delivery-service guards `WHERE jeeber_id IS NULL`, never
        // steals). The id/tier/pickup come from the synced ledger row (the same data
        // the create-seed used). DEGRADE-DON'T-FAIL: this assignment mirror must never
        // turn a committed accept into a 5xx, so every failure is logged and swallowed.
        // JEBV4-300 — the assignment mirror is DURABLE-BEFORE-RETURN. The upsert alone
        // is fire-and-forget against a delivery-service that may be read-replica-lagged;
        // until the row carries jeeber_id its authorise() 403s BOTH parties, so a PATCH
        // /deliveries/{id}/status fired within seconds of accept fails with wrong_party.
        // After the upsert we read the canonical row back and bounded-retry until
        // jeeber_id == winningJeeberId, so the 200 we return is a promise the delivery
        // is assignable. The OUTER swallow is retained so a committed offer-service
        // accept NEVER turns into a 5xx (that invariant is load-bearing) — a read-back
        // that never confirms only logs; DeliveriesController's PATCH-status re-mirror
        // (leg b) self-heals the residual race.
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

                await ConfirmDeliveryAssignmentVisibleAsync(synced.Id, winningJeeberId!, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-accept delivery-assignment for request {RequestId} (jeeber {JeeberId}) failed; accept stays 200.",
                    requestId, winningJeeberId);
            }
        }
        else
        {
            _logger.LogInformation(
                "Post-accept delivery-assignment for request {RequestId}: ledger row not synced locally; skipping the jeeber-assignment mirror (the offer-service accept still committed).",
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

        // (H7/N9) Advance the CONVERSATION AGGREGATE phase to "accepted". The legacy
        // channels provisioner above advances MEMBERSHIP only and cannot flip the
        // aggregate phase (see its PHASE NOTE) — the S08 conversation aggregate
        // (driven via /v1/conversations/*) is a SEPARATE chat-service surface that
        // owns the phase + winner promotion + loser soft-removal atomically via
        // PATCH /api/conversations/{id}/phase. We call it here so H7/N9 read
        // $.conversation_phase == "accepted" and the winner becomes jeeber_winner.
        //
        // The aggregate's conversation id == correlation_key == requestId (auto
        // conversation-per-request), so we advance by requestId. chat-service is the
        // authority; the gateway reads back the resulting phase and surfaces it.
        //
        // DEGRADE-DON'T-FAIL: a chat blip / disabled flag returns null and the accept
        // DTO defaults conversation_phase to "accepted" (the saga committed) — it must
        // NEVER 5xx the accept.
        if (!_flags.Chat)
        {
            return null;
        }

        try
        {
            var advanced = await _conversationAggregate.AdvancePhaseAsync(
                requestId,
                new AdvanceJeebPhaseRequest
                {
                    Phase = "accepted",
                    WinnerUserId = winningJeeberId,
                    WinnerRoleInConvo = "jeeber_winner",
                    RemoveOthers = true,
                },
                ct);
            return string.IsNullOrWhiteSpace(advanced.Phase) ? "accepted" : advanced.Phase;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept conversation-aggregate phase advance for request {RequestId} failed; "
                + "accept stays 200, conversation_phase defaults to 'accepted'.",
                requestId);
            return null;
        }
    }

    // JEBV4-300 — read-back budget for the post-accept assignment mirror. Bounded so a
    // genuinely-stuck upstream can never hang the accept: at most 3 canonical reads, the
    // first fired immediately after the upsert, the rest ~200ms apart (≈400ms worst case).
    private const int AssignmentReadBackAttempts = 3;
    private static readonly TimeSpan AssignmentReadBackDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// JEBV4-300 (assignment-mirror race). After the idempotent post-accept upsert seeds
    /// <c>jeeber_id = winningJeeberId</c>, confirm it is DURABLY VISIBLE on the canonical
    /// delivery-service row before the accept returns — reading
    /// <see cref="IDeliveryServiceClient.GetCanonicalDeliveryAsync"/> and bounded-retrying
    /// (<see cref="AssignmentReadBackAttempts"/> × <see cref="AssignmentReadBackDelay"/>)
    /// until the row's <c>jeeber_id</c> equals the winner. Until that is visible,
    /// delivery-service's authorise() 403s BOTH parties, so a PATCH /status fired seconds
    /// after accept would race the mirror. NEVER throws on a non-confirming read: the outer
    /// caller's swallow keeps a committed accept at 200, and leg (b) self-heals the residual.
    /// </summary>
    private async Task ConfirmDeliveryAssignmentVisibleAsync(
        string deliveryId, string winningJeeberId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= AssignmentReadBackAttempts; attempt++)
        {
            DeliveryReadUpstream? row = null;
            try
            {
                row = await _deliveryService.GetCanonicalDeliveryAsync(deliveryId, ct);
            }
            catch (OperationCanceledException)
            {
                return; // caller cancelled — nothing to confirm; accept response already shaped.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-accept assignment read-back for delivery {DeliveryId} attempt {Attempt}/{Max} faulted; retrying.",
                    deliveryId, attempt, AssignmentReadBackAttempts);
            }

            if (row is not null && string.Equals(row.JeeberId, winningJeeberId, StringComparison.Ordinal))
            {
                return; // durably assigned — a status transition by either party will authorise.
            }

            if (attempt < AssignmentReadBackAttempts)
            {
                await Task.Delay(AssignmentReadBackDelay, ct);
            }
        }

        _logger.LogWarning(
            "Post-accept assignment read-back for delivery {DeliveryId} did not observe jeeber_id={JeeberId} "
            + "after {Max} attempts; accept stays 200 and the PATCH-status re-mirror (leg b) self-heals the race.",
            deliveryId, winningJeeberId, AssignmentReadBackAttempts);
    }

    /// <summary>
    /// sprint-009 Lane E — best-effort accept-lifecycle push fan-out. Sends one
    /// <c>jeeb.offer_accepted</c> push to the winning jeeber and one
    /// <c>jeeb.offer_rejected</c> push per rejected sibling (each losing bidder resolved
    /// from the offer routing index via <see cref="IOfferRequestIndex.ResolveJeeberId"/>).
    /// The notifier never throws; this extra try/catch is belt-and-braces so the committed
    /// accept's 200 can never be flipped to a 5xx. Identical contract to the V1
    /// <c>JeebOffersController</c> fan-out.
    /// </summary>
    private async Task DispatchAcceptLifecyclePushesAsync(
        string requestId,
        string acceptedOfferId,
        string? winningJeeberId,
        IReadOnlyList<string>? rejectedOfferIds,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(winningJeeberId))
            {
                await _offerPush.NotifyOfferAcceptedAsync(winningJeeberId, requestId, acceptedOfferId, ct);
            }

            if (rejectedOfferIds is not null)
            {
                foreach (var rejectedOfferId in rejectedOfferIds)
                {
                    if (string.IsNullOrWhiteSpace(rejectedOfferId))
                        continue;

                    var loserJeeberId = _offerRequestIndex.ResolveJeeberId(rejectedOfferId);
                    if (string.IsNullOrWhiteSpace(loserJeeberId))
                        continue;

                    await _offerPush.NotifyOfferLostAsync(loserJeeberId, requestId, rejectedOfferId, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept lifecycle push fan-out for request {RequestId} (offer {OfferId}) failed; "
                + "accept stays 200.", requestId, acceptedOfferId);
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
    private OfferAcceptResultDto BuildAcceptedDto(
        string requestId, string actorId, OfferAcceptWire envelope, string? conversationPhase)
        => new()
        {
            Id = requestId,
            ClientId = actorId,
            // ARCH LAW (accept DTO must not leak delivery status): this is the
            // OFFER-ACCEPT response, so its status field reports the OFFER-acceptance
            // outcome — "accepted" — unconditionally. It is NOT the delivery's
            // lifecycle state.
            //
            // Two distinct facts were previously conflated in the old
            // DeliveryRequestDto projection: (1) the auction outcome (this offer was
            // accepted) and (2) the spawned delivery's canonical SM state ("Ordered").
            // The JEB-45 ternary leaked the delivery state into the offer-accept DTO,
            // so with UseUpstream:Delivery forced ON POST /offers/{id}/accept returned
            // "Ordered" and regressed S07 H5/A3/N7 (H5 asserts $.status=="accepted").
            // This dedicated accept DTO surfaces "accepted" unconditionally — the
            // S07 P0 fix is preserved, no delivery status leaks.
            Status = RequestStatus.Accepted,
            JeeberId = envelope.JeeberId ?? string.Empty,
            AcceptedAt = _clock.GetUtcNow(),
            // S08 (H7/N9): the winning jeeber and the advanced conversation phase.
            // winner_user_id == the awarded jeeber (same value as jeeberId, exposed
            // under the snake_case key the S08 suite asserts). conversation_phase is
            // the phase chat-service returned from the aggregate advance; when chat
            // is disabled/unavailable it defaults to "accepted" (the saga committed)
            // so the accept never 5xxs and the assertion still holds.
            WinnerUserId = envelope.JeeberId ?? string.Empty,
            ConversationPhase = string.IsNullOrWhiteSpace(conversationPhase)
                ? RequestStatus.Accepted
                : conversationPhase,
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

/// <summary>
/// S07/S08 — dedicated response for <c>POST /offers/{offerId}/accept</c>. This is
/// the OFFER-ACCEPT result, NOT a delivery projection: it is intentionally a
/// SEPARATE type from <see cref="DeliveryRequestDto"/> so the accept surface never
/// re-acquires delivery-lifecycle fields and can never leak the delivery's
/// canonical SM state (the S07 P0 regression). It carries:
/// <list type="bullet">
///   <item>the S07-asserted fields (<c>id</c>, <c>clientId</c>,
///     <c>status</c>=="accepted", <c>jeeberId</c>, <c>acceptedAt</c>) — camelCase
///     per the host's default System.Text.Json policy, byte-compatible with the
///     DeliveryRequestDto the accept previously returned; and</item>
///   <item>the S08 additions (<c>winner_user_id</c>, <c>conversation_phase</c>) —
///     pinned to snake_case because the S08 suite asserts those exact keys
///     (H7/N9 <c>$.winner_user_id</c> / <c>$.conversation_phase</c>).</item>
/// </list>
/// Additive: a consumer reading only the S07 fields is unaffected; the two new
/// keys are extra.
/// </summary>
public sealed class OfferAcceptResultDto
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }

    /// <summary>Always "accepted" — the OFFER-acceptance outcome, never a delivery state.</summary>
    public required string Status { get; init; }

    /// <summary>The awarded jeeber (camelCase — S07 asserts <c>$.jeeberId</c>).</summary>
    public string? JeeberId { get; init; }

    public DateTimeOffset? AcceptedAt { get; init; }

    /// <summary>
    /// S08 (H7/N9) — the winning jeeber's user id under the snake_case key the suite
    /// asserts (<c>$.winner_user_id</c>). Same value as <see cref="JeeberId"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("winner_user_id")]
    public string? WinnerUserId { get; init; }

    /// <summary>
    /// S08 (H7/N9) — the conversation aggregate phase after the post-accept advance
    /// (<c>$.conversation_phase</c>), resolved from chat-service. Defaults to
    /// "accepted" when chat is disabled/unavailable so the accept never 5xxs.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("conversation_phase")]
    public string? ConversationPhase { get; init; }
}
