using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations.Client;
using JeebGateway.Notifications;
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
    private readonly IJeebConversationClient _conversations;
    private readonly IOfferPushNotifier _offerPush;
    private readonly UpstreamFeatureFlags _flags;
    private readonly DeliveryClientOptions _deliveryOptions;
    private readonly ILogger<JeebOffersController> _logger;

    public JeebOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IDeliveryServiceClient deliveryService,
        IJeebConversationClient conversations,
        IOfferPushNotifier offerPush,
        IOptions<UpstreamFeatureFlags> flags,
        IOptions<DeliveryClientOptions> deliveryOptions,
        ILogger<JeebOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _deliveryService = deliveryService;
        _conversations = conversations;
        _offerPush = offerPush;
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
            OfferAcceptStatus.Accepted => await BuildAcceptedResponseAsync(requestId, offerId, result, ct),
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
        string offerId,
        OfferAcceptResult result,
        CancellationToken ct)
    {
        var req = await _requests.GetAsync(requestId, ct);

        // P0 — resolve the WINNING jeeber id once, with precedence:
        //   (a) the offer-service accept envelope's actor/jeeber id, when present;
        //   (b) else the bidder recorded in the offer routing index at offer-submit
        //       (IOfferRequestIndex.ResolveJeeberId) — the live fallback, because the
        //       offer-service accept response is observed to omit actor_id/jeeber_id,
        //       leaving (a) null. (A direct offer-service get-offer-by-id lookup is not
        //       available — offer-service exposes no such route, per OfferRequestIndex.)
        // This single resolved id feeds BOTH the delivery-leg sync, the chat seat, AND
        // the local read-model JeeberId stamp below — without it the local row's JeeberId
        // stayed null on the upstream path and ListForJeeberAsync returned the jeeber an
        // empty Jobs/Deliveries list.
        var winningJeeberId = result.Envelope?.JeeberId;
        if (string.IsNullOrWhiteSpace(winningJeeberId))
            winningJeeberId = _offerRequestIndex.ResolveJeeberId(offerId);

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
        await SyncDeliveryLegAsync(req, winningJeeberId, ct);

        // S03 — project the accepted state onto the gateway's local read-model. GET
        // /v1/requests/{id} (JeebRequestsController.Get) reads ONLY _requests, so the
        // upstream accept path — which previously left the local row at its pre-accept
        // status (pending/matched) — made the client poll "pending" forever even though
        // the offer-service saga had committed the canonical accept. Mirror what the
        // in-memory AcceptInMemoryAsync path does via TryAcceptByJeeberAsync.
        // DEGRADE-DON'T-FAIL: the saga already committed upstream, so a local projection
        // miss is logged, never a 5xx; we re-read so the 200 body reflects the new status.
        try
        {
            if (await _requests.SetStatusAsync(requestId, RequestStatus.Accepted, ct))
                req = await _requests.GetAsync(requestId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept status projection for request {RequestId} failed; accept stays 200, "
                + "the read-model may lag until reconciled.", requestId);
        }

        // P0 — stamp the WINNING jeeber onto the local read-model row. This is the WRITE
        // counterpart to ListForJeeberAsync: the upstream accept path projects only the
        // STATUS (above) and never wrote the assignee, so the jeeber's Jobs/Deliveries
        // list (GET /v1/deliveries, GET /v1/requests?role=jeeber) came back empty. The
        // legacy in-memory path stamped JeeberId via TryAcceptByJeeberAsync; mirror it
        // here for the upstream composer. DEGRADE-DON'T-FAIL: the saga already committed,
        // so a stamp miss is logged, never a 5xx; we re-read so the 200 body and the
        // jeeber list reflect the assignment. SetJeeberIdAsync no-ops on a blank id, so a
        // missing upstream actor id never clears a previously-resolved jeeber.
        if (!string.IsNullOrWhiteSpace(winningJeeberId))
        {
            try
            {
                if (await _requests.SetJeeberIdAsync(requestId, winningJeeberId, ct))
                    req = await _requests.GetAsync(requestId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-accept jeeber-id projection for request {RequestId} (jeeber {JeeberId}) "
                    + "failed; accept stays 200 — the jeeber's Jobs list may lag until reconciled.",
                    requestId, winningJeeberId);
            }
        }

        // S03 P1 — ensure the chat conversation EXISTS, then seat the winning jeeber.
        // The accept saga commits the single-winner transition but holds no chat client
        // (org no-coupling law), and at this point the request may have NO conversation
        // (auto-create was off / chat was down at order-create), so a seat attempted before
        // the conversation exists fails and the winning jeeber reads 403 on chat. The
        // gateway — the SOLE chat caller — resolves-or-creates the conversation, links its
        // id onto the local projection, THEN seats the jeeber (correct ordering).
        await EnsureConversationAndSeatWinnerAsync(req, winningJeeberId, ct);

        // sprint-009 Lane E — the accept-lifecycle push fan-out. The offer-service accept
        // saga closes the auction (single winner + sibling rejection) but owns NO Jeeb
        // notification (org no-coupling law), so the gateway is the composer: it pushes
        // (a) jeeb.offer_accepted to the WINNING jeeber and (b) jeeb.offer_rejected to each
        // LOSING bidder named in the envelope's RejectedOfferIds. DEGRADE-DON'T-FAIL: the
        // saga already committed and the 200 is emitted, so a push blip is logged and
        // swallowed — it must never flip a successful accept into a 5xx.
        await DispatchAcceptLifecyclePushesAsync(requestId, offerId, winningJeeberId, result.Envelope?.RejectedOfferIds, ct);

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
    /// sprint-009 Lane E — best-effort accept-lifecycle push fan-out. Sends exactly one
    /// <c>jeeb.offer_accepted</c> push to the winning jeeber and one
    /// <c>jeeb.offer_rejected</c> push per rejected sibling (resolving each losing bidder
    /// from the offer routing index via <see cref="IOfferRequestIndex.ResolveJeeberId"/>).
    /// The notifier itself never throws; this extra try/catch is belt-and-braces so even a
    /// bug in the fan-out can NEVER flip the committed accept's 200 into a 5xx. Mirrors the
    /// degrade-don't-fail contract of the offer-submit push seat.
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

                    // Resolve the losing bidder from the routing index learned at submit
                    // time. A null result (offer unknown to this instance / recorded without
                    // a jeeber id) means we cannot address the push — skip it, never guess.
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
    /// S03 P1 — post-accept chat readiness. Ensures the request's conversation exists
    /// (resolve by correlation key == requestId, else create it in chat-service with the
    /// snake_case <c>correlation_key</c>/<c>owner_user_id</c> shape — chat-service is the
    /// authority and idempotent on the correlation key), links the resolved id onto the
    /// local request projection so <c>GET /v1/requests/{id}</c> and the Orders/Jobs lists
    /// surface a non-null <c>conversationId</c>, THEN seats the winning jeeber as a
    /// <c>jeeber_winner</c> participant so they can open chat without a 403.
    ///
    /// <para>Fixes the ordering defect: the previous offer-submit seat ran before any
    /// conversation existed (auto-create off / chat down at create), so the seat failed and
    /// the jeeber 403'd. Creating the conversation here — at accept — guarantees it exists
    /// before the winner is seated.</para>
    ///
    /// <para>The gateway is the SOLE chat caller (org no-coupling law) and computes NO
    /// membership; it forwards (correlation_key, owner_user_id) and (conversationId, userId,
    /// role) to chat-service. DEGRADE-DON'T-FAIL: the accept saga already committed, so a
    /// chat blip / disabled flag / lookup miss is logged and swallowed — never a 5xx; the
    /// jeeber reads 403 until reconciled, exactly as before. Gated on the Chat upstream
    /// flag, mirroring the offer-submit seat.</para>
    /// </summary>
    private async Task EnsureConversationAndSeatWinnerAsync(
        DeliveryRequest? request, string? winningJeeberId, CancellationToken ct)
    {
        if (!_flags.Chat)
        {
            return;
        }
        if (request is null || string.IsNullOrWhiteSpace(winningJeeberId))
        {
            return;
        }

        var requestId = request.Id;
        try
        {
            // 1) Resolve the conversation: prefer the id already on the ledger row; else the
            //    chat-service by-correlation lookup (a client may have created it explicitly);
            //    else create it. chat-service de-dups on correlation_key, so a racing create is
            //    safe (INV-3: replay returns the same conversation_id).
            var conversationId = request.ConversationId;

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                try
                {
                    var existing = await _conversations.GetConversationByCorrelationAsync(requestId, ct);
                    conversationId = existing?.ConversationId;
                }
                catch (JeebConversationApiException)
                {
                    // 404 / not-yet-created — fall through to create.
                }
            }

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                var created = await _conversations.CreateConversationAsync(new CreateJeebConversationRequest
                {
                    RequestId = requestId,            // -> correlation_key (idempotency authority)
                    ClientUserId = request.ClientId,  // -> owner_user_id (seeded role_in_convo = client)
                    IdempotencyKey = requestId,
                }, ct);
                conversationId = created?.ConversationId;
            }

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                _logger.LogWarning(
                    "Post-accept conversation ensure for request {RequestId}: no conversationId "
                    + "resolvable or creatable; jeeber {JeeberId} stays unseated until reconciled "
                    + "(accept stays 200).", requestId, winningJeeberId);
                return;
            }

            // 2) Link the conversation id onto the local projection (in-place stamp on the
            //    live store row, mirroring the create-time auto-create path) so subsequent
            //    reads surface a non-null conversationId.
            request.ConversationId = conversationId;

            // 3) Seat the winning jeeber so they can open chat without a 403.
            await _conversations.AddParticipantAsync(
                conversationId,
                new AddJeebParticipantRequest
                {
                    UserId = winningJeeberId,
                    RoleInConvo = "jeeber_winner",
                },
                ct);

            // 4) Advance the conversation OUT of the auction/broadcasting phase into the
            //    settled (accepted) 1:1: promote the winner's role and soft-remove the
            //    losing bidders. This is the second half of the winner-blind-to-client
            //    fix — chat-service only grants the winner visibility of the client's
            //    messages in a CLEAN 1:1 (owner + single accepted counterpart), so this
            //    transition is what makes that safe (no losing bidder is left seated to
            //    ever see the client's private text). Idempotent on the chat side; the
            //    winner_role_in_convo token ("jeeber_winner") is the one the winner is
            //    seated with above, so the promotion recognises it.
            //    DEGRADE-DON'T-FAIL: still inside the accept saga's post-commit best-effort
            //    block — a chat blip is logged and swallowed (accept stays 200); the phase
            //    reconciles on a later pass.
            await _conversations.AdvancePhaseAsync(
                conversationId,
                new AdvanceJeebPhaseRequest
                {
                    Phase = "accepted",
                    WinnerUserId = winningJeeberId,
                    WinnerRoleInConvo = "jeeber_winner",
                    RemoveOthers = true,
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept conversation ensure/seat for request {RequestId} failed; accept stays "
                + "200, jeeber {JeeberId} may read 403 on chat until reconciled.",
                requestId, winningJeeberId);
        }
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
