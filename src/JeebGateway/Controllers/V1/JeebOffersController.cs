using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
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
    private readonly IConversationProvisioner _conversations;
    private readonly IJeebConversationClient _conversationAggregate;
    private readonly DeliveryClientOptions _deliveryOptions;
    private readonly TimeProvider _clock;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<JeebOffersController> _logger;

    public JeebOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IDeliveryServiceClient deliveryService,
        IConversationProvisioner conversations,
        IJeebConversationClient conversationAggregate,
        IOptions<DeliveryClientOptions> deliveryOptions,
        TimeProvider clock,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<JeebOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _deliveryService = deliveryService;
        _conversations = conversations;
        _conversationAggregate = conversationAggregate;
        _deliveryOptions = deliveryOptions.Value;
        _clock = clock;
        _flags = flags.Value;
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
            // actorId is the request-owning CLIENT (OfferAccept is an {client}
            // capability — ADR-005 L2/S07; offer-service returns NotOwner otherwise),
            // so it is the authoritative client_id for the delivery mint below.
            OfferAcceptStatus.Accepted => await BuildAcceptedResponseAsync(requestId, actorId, result, ct),
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
        string acceptingClientId,
        OfferAcceptResult result,
        CancellationToken ct)
    {
        // B5 (iter6 orderflow) — the offer-service accept saga is the authority for
        // the single-winner OFFER transition and has committed upstream. But it owns
        // ONLY that generic transition (JEB-1474): the Jeeb-domain CROSS-SERVICE
        // composition — flipping the gateway's own request ledger to accepted, minting
        // /assigning the winning jeeber onto the durable delivery row, and advancing
        // the broadcasting conversation — is the GATEWAY's job. The legacy
        // OffersController already does this in OrchestrateAcceptedAsync; the V1 BFF
        // accept (the route the mobile app actually calls, POST /v1/offers/{id}/accept)
        // previously only re-read the request, so the request stayed `pending` and NO
        // delivery was minted from the accept (residual B5). We run the SAME thin
        // post-accept saga here.
        //
        // B5-RELIABILITY (iter6 fix) — the delivery MINT is the ONE functional
        // side-effect that MUST succeed: it is what makes the create→accept→delivery
        // chain real. It is NO LONGER best-effort/degrade-don't-fail. The earlier
        // version gated the mint on the in-memory request-flip landing locally
        // (synced != null), so for a request whose ledger row was not warm in THIS
        // gateway instance's in-memory store (post-restart / rehydrate-timing /
        // cross-flow) the mint was SKIPPED ENTIRELY and accept returned a silent 200
        // with no delivery — the intermittent failure. Now the mint runs
        // unconditionally (resolving its inputs from the rehydrated request row, B9)
        // and a genuine, retried mint failure SURFACES as a clear non-2xx instead of
        // a silent 200. The chat/conversation advance stays degrade-don't-fail (it is
        // cosmetic to the order chain).
        var outcome = result.Envelope is null
            ? AcceptOrchestrationOutcome.MissingEnvelope()
            : await OrchestrateAcceptedAsync(requestId, acceptingClientId, result.Envelope, ct);

        // The MINT is mandatory. If it could not be completed (and was genuinely
        // attempted), surface a clear failure so the client retries the accept
        // (idempotent upstream) rather than silently believing it succeeded. The
        // offer-service accept already committed, so a replayed accept collapses
        // cleanly and the mint is idempotent (ON CONFLICT (id) DO NOTHING / late
        // jeeber-assign WHERE jeeber_id IS NULL).
        if (!outcome.DeliveryMinted)
        {
            _logger.LogError(
                "B5 accept for request {RequestId} (jeeber {JeeberId}) committed the OFFER upstream but could NOT mint the delivery row ({Reason}); surfacing 502 so the client retries (accept is idempotent).",
                requestId, result.Envelope?.JeeberId, outcome.FailureReason ?? "unknown");

            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Offer accepted upstream but the delivery could not be created. Please retry.",
                Detail = outcome.FailureReason,
                Status = StatusCodes.Status502BadGateway,
                Type = "https://jeeb.dev/errors/delivery-mint-failed",
                Extensions =
                {
                    ["requestId"] = requestId,
                    ["acceptedOfferId"] = result.Envelope?.AcceptedOfferId,
                    ["jeeberId"] = result.Envelope?.JeeberId,
                }
            });
        }

        if (outcome.SyncedRequest is not null)
            return Ok(ToRequestDto(outcome.SyncedRequest));

        var req = await _requests.GetAsync(requestId, ct);
        if (req is not null)
            return Ok(ToRequestDto(req));

        // Request not in local store (delivery-service is the SoT). The delivery WAS
        // minted (checked above) — return a minimal acknowledgement so the client
        // knows acceptance succeeded and the delivery exists.
        return Ok(new
        {
            requestId,
            acceptedOfferId = result.Envelope?.AcceptedOfferId,
            jeeberId = result.Envelope?.JeeberId,
            status = "accepted"
        });
    }

    /// <summary>
    /// B5 post-accept BFF orchestration (thin saga). Runs AFTER the offer-service
    /// accept saga returns <see cref="OfferAcceptStatus.Accepted"/> and BEFORE the
    /// 200 is emitted. Ported verbatim from the legacy
    /// <see cref="JeebGateway.Controllers.OffersController"/>'s
    /// <c>OrchestrateAcceptedAsync</c> so the live mobile accept path
    /// (<c>POST /v1/offers/{id}/accept</c>) propagates the accept the SAME way:
    /// <list type="number">
    ///   <item><b>request-flip</b> — flips the gateway's request ledger row to
    ///     <c>accepted</c> + winning <c>jeeberId</c> via the existing atomic
    ///     <see cref="IRequestsStore.TryAcceptByJeeberAsync"/>, so
    ///     <c>GET /v1/requests/{id}</c> reflects the accepted state and a real
    ///     delivery is tied to the request.</item>
    ///   <item><b>delivery-mint/assign</b> — re-POSTs the same delivery id carrying
    ///     <c>jeeber_id = winner</c> via the idempotent
    ///     <see cref="IDeliveryServiceClient.CreateDeliveryRowAsync"/> (ON CONFLICT
    ///     (id) DO NOTHING for create; late-assign the jeeber WHERE jeeber_id IS NULL).
    ///     This MINTS the canonical delivery row from the accepted request+offer when
    ///     it was not seeded at create, and assigns the winning jeeber so the SAME
    ///     delivery flows Ordered→…→Done and counts toward BR-10.</item>
    ///   <item><b>conversation advance</b> — adds the winning jeeber to the
    ///     broadcasting conversation, deactivates losers, and advances the
    ///     conversation-aggregate phase to <c>accepted</c>.</item>
    /// </list>
    /// EVERY step is wrapped so a downstream failure is logged and swallowed — a
    /// successful upstream accept must remain a 200 even if a side-effect blips.
    /// </summary>
    /// <returns>An <see cref="AcceptOrchestrationOutcome"/> carrying whether the
    /// mandatory delivery MINT succeeded (the caller surfaces a non-2xx if not) and
    /// the synced request ledger row when the local flip landed (else null — the
    /// caller falls back to a re-read / minimal ack).</returns>
    private async Task<AcceptOrchestrationOutcome> OrchestrateAcceptedAsync(
        string requestId, string acceptingClientId, OfferAcceptWire envelope, CancellationToken ct)
    {
        var winningJeeberId = envelope.JeeberId;
        if (string.IsNullOrWhiteSpace(winningJeeberId))
        {
            // No winner id on the envelope → we cannot assign a jeeber onto a
            // delivery. This is a structural fault on the upstream accept (it
            // returned 200 but omitted the winner), not a transient blip — surface
            // it so the chain is never reported as complete with no winner/delivery.
            _logger.LogError(
                "B5 post-accept orchestration for request {RequestId}: upstream accept envelope carried no jeeberId; cannot mint the delivery.",
                requestId);
            return AcceptOrchestrationOutcome.MintFailed(
                synced: null,
                reason: "Upstream accept envelope carried no winning jeeberId; the delivery could not be assigned.");
        }

        var now = _clock.GetUtcNow();

        // (1) Flip the gateway's own request ledger row. TryAcceptByJeeberAsync is
        // the existing atomic setter (status=accepted + jeeberId + acceptedAt under
        // the inner store's write lock). BR-9/race-safety stays in the offer-service —
        // this is a ledger mirror, not a second authority. DEGRADE-DON'T-FAIL: this is
        // only the fast read-model mirror; a miss here must NOT skip the mint (that was
        // the intermittent B5 bug). When it returns null the row simply isn't warm in
        // THIS instance's in-memory store — the durable delivery-service row (read
        // back below via GetAsync's B9 rehydrate) is the SoT either way.
        DeliveryRequest? synced = null;
        try
        {
            synced = await _requests.TryAcceptByJeeberAsync(
                requestId, winningJeeberId, ActiveDeliveriesLimit, now, ct);
            if (synced is null)
            {
                _logger.LogInformation(
                    "B5 post-accept request-flip for {RequestId}: ledger row not warm in this gateway instance; the mint resolves its inputs from the durable delivery-service row instead.",
                    requestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "B5 post-accept request-flip for {RequestId} failed; continuing to the mint (the delivery-service row is the SoT).",
                requestId);
        }

        // (2) MINT / ASSIGN the winning jeeber onto the canonical delivery row — the
        // MANDATORY step. Resolve the mint inputs from the warm ledger row when we
        // have it, ELSE from a read-through (when DurableRequests is ON,
        // DurableRequestsStore.GetAsync rehydrates the row from delivery-service, B9).
        // This decouples the mint from the in-memory flip so it runs RELIABLY whether
        // or not the row was warm — fixing the intermittent skip. The create-row
        // endpoint is idempotent (ON CONFLICT (id) DO NOTHING for create; late-assign
        // jeeber WHERE jeeber_id IS NULL), so this both seeds a row that was never
        // created at request-time AND assigns the winner — the SAME delivery
        // id == requestId flows Ordered→…→Done. A genuine, retried failure SURFACES.
        var mintRow = synced;
        if (mintRow is null || string.IsNullOrWhiteSpace(mintRow.ClientId))
        {
            // Read-through so we have tier/pickup for a from-scratch mint. With
            // DurableRequests ON this rehydrates from delivery-service; with it OFF
            // (live default) it is the same in-memory miss — that is fine, because the
            // client_id below is sourced authoritatively from the accepting caller.
            try
            {
                var rehydrated = await _requests.GetAsync(requestId, ct);
                if (rehydrated is not null && !string.IsNullOrWhiteSpace(rehydrated.ClientId))
                {
                    mintRow = rehydrated;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "B5 post-accept mint for {RequestId}: request read-through failed; minting from the accepting client + envelope.",
                    requestId);
            }
        }

        // client_id is REQUIRED by the delivery-service create endpoint, and the row
        // may not be resolvable from any store (in-memory miss + DurableRequests OFF
        // ⇒ no pre-seeded delivery row). The accepting caller IS the request-owning
        // client (OfferAccept is an {client} capability; the offer-service already
        // enforced ownership before returning Accepted), so it is the AUTHORITATIVE,
        // store-independent client_id — this is what makes the mint reliable for cold
        // rows. Prefer the resolved row's client_id when present (identical value),
        // else fall back to the accepting client.
        var mintClientId = !string.IsNullOrWhiteSpace(mintRow?.ClientId)
            ? mintRow!.ClientId
            : acceptingClientId;

        var mintReason = await TryMintDeliveryAsync(requestId, winningJeeberId, mintClientId, mintRow, ct);
        var minted = mintReason is null;

        // (3) Advance the broadcasting conversation: add the winning jeeber, drop
        // losers. The provisioner is the SOLE chat caller and degrades to null on any
        // chat blip, so this never blocks the accept. DEGRADE-DON'T-FAIL (cosmetic to
        // the order chain — the mint above is what the chain depends on).
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
                "B5 post-accept conversation advance for {RequestId} (conversation {ConversationId}) failed; accept proceeds.",
                requestId, conversationId);
        }

        // (4) Advance the conversation-aggregate phase to "accepted" (winner promotion
        // + loser soft-removal). DEGRADE-DON'T-FAIL: a chat blip / disabled flag is
        // swallowed and never blocks the accept.
        if (_flags.Chat)
        {
            // FIX (iter6 GAP A2): chat-service ConversationService.AdvancePhaseAsync
            // resolves the aggregate by CONVERSATION ID (GetConversationAsync(id)),
            // NOT by correlation key. This path previously passed the requestId
            // (== correlation key), so the PATCH 404'd (swallowed) and the
            // conversation never left phase "broadcasting" — the live cross-device
            // chat stayed in the pre-accept state. Use the conversation id resolved
            // above (synced.ConversationId / by-correlation read); fall back to
            // requestId only when no conversation id is resolvable at all.
            var phaseTargetId = string.IsNullOrWhiteSpace(conversationId) ? requestId : conversationId;
            try
            {
                await _conversationAggregate.AdvancePhaseAsync(
                    phaseTargetId,
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
                    "B5 post-accept conversation-aggregate phase advance for request {RequestId} (conversation {ConversationId}) failed; accept proceeds.",
                    requestId, conversationId);
            }
        }

        return minted
            ? AcceptOrchestrationOutcome.MintSucceeded(synced)
            : AcceptOrchestrationOutcome.MintFailed(synced, mintReason);
    }

    /// <summary>
    /// B5-RELIABILITY: the MANDATORY delivery mint/assign with a bounded retry. The
    /// delivery-service create-row endpoint is idempotent (ON CONFLICT (id) DO NOTHING
    /// for the row; late-assign the jeeber WHERE jeeber_id IS NULL — never steals), so
    /// re-POSTing the SAME id is always safe — both to seed a row that was never
    /// created at request-time AND to assign the winning jeeber on an already-seeded
    /// row. The endpoint REQUIRES a non-empty client_id; the gateway resolves it from
    /// the (warm or rehydrated) request row. tier/pickup are only needed for a
    /// from-scratch insert — on the common path the row already exists from the
    /// create-seed, so the upsert only late-assigns the jeeber and tier/pickup are
    /// inert.
    /// </summary>
    /// <returns><c>null</c> when the delivery was minted/assigned; a human-readable
    /// failure reason otherwise (the caller surfaces it as a non-2xx).</returns>
    private async Task<string?> TryMintDeliveryAsync(
        string requestId, string winningJeeberId, string clientId, DeliveryRequest? row, CancellationToken ct)
    {
        // The delivery id is the request id (stable across create→accept→delivery).
        var deliveryId = string.IsNullOrWhiteSpace(row?.Id) ? requestId : row!.Id;

        // client_id is REQUIRED by the create endpoint; the caller resolves it from
        // the request row when known, else from the accepting client (authoritative —
        // the acceptor IS the owning client). It is never empty on this path, but
        // guard defensively so we surface a clear reason rather than a 400 loop.
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return $"Could not resolve a client_id for {requestId}; the delivery row could not be created (required by delivery-service).";
        }

        var body = new CreateDeliveryRowUpstream
        {
            Id = deliveryId,
            TenantId = _deliveryOptions.TenantId,
            ClientId = clientId,
            JeeberId = winningJeeberId,
            TierId = row?.TierId ?? string.Empty,
            PickupLat = row?.PickupLocation?.Lat ?? 0d,
            PickupLng = row?.PickupLocation?.Lng ?? 0d,
        };

        const int maxAttempts = 3;
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _deliveryService.CreateDeliveryRowAsync(body, ct);
                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "B5 post-accept delivery mint for request {RequestId} (jeeber {JeeberId}) succeeded on attempt {Attempt}.",
                        requestId, winningJeeberId, attempt);
                }
                return null; // minted/assigned (idempotent).
            }
            catch (OperationCanceledException)
            {
                throw; // caller cancellation is not a mint failure to retry.
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(ex,
                    "B5 post-accept delivery mint for request {RequestId} (jeeber {JeeberId}) attempt {Attempt}/{MaxAttempts} failed.",
                    requestId, winningJeeberId, attempt, maxAttempts);
                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        return $"delivery-service create-row failed after {maxAttempts} attempts: {last?.Message}";
    }

    /// <summary>
    /// Resolves the broadcasting conversation id stamped on the request ledger row
    /// without throwing — a read failure degrades to <c>null</c> so the accept
    /// orchestration continues.
    /// </summary>
    private async Task<string?> SafeGetConversationIdAsync(string requestId, CancellationToken ct)
    {
        // Prefer the conversation id stamped on the gateway's request ledger row
        // (the order-create auto-create path stamps it there).
        try
        {
            var row = await _requests.GetAsync(requestId, ct);
            if (!string.IsNullOrWhiteSpace(row?.ConversationId))
            {
                return row!.ConversationId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "B5 post-accept conversation-id read for {RequestId} failed; falling back to the by-correlation lookup.",
                requestId);
        }

        // FIX (iter6 GAP A2): the live mobile flow creates the AGGREGATE conversation
        // itself (POST /v1/chat/jeeb/conversations) and it is NOT stamped on the
        // request ledger row (ConversationAutoCreate is off), so the ledger read above
        // returns null. Resolve the real conversation id from chat-service by
        // correlation key (== requestId) — the SAME source SeatOfferingJeeberAsync
        // uses — so the post-accept phase advance targets the right conversation id
        // (passing requestId-as-id would 404). Degrade to null on any chat blip; the
        // caller then leaves the phase unchanged rather than 5xx-ing the accept.
        if (_flags.Chat)
        {
            try
            {
                var convo = await _conversationAggregate.GetConversationByCorrelationAsync(requestId, ct);
                if (!string.IsNullOrWhiteSpace(convo?.ConversationId))
                {
                    return convo!.ConversationId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "B5 post-accept conversation-id by-correlation lookup for {RequestId} failed; advancing without a conversation id.",
                    requestId);
            }
        }

        return null;
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

    /// <summary>
    /// B5-RELIABILITY: the result of the post-accept BFF orchestration. Carries the
    /// one fact the response builder must branch on — whether the MANDATORY delivery
    /// MINT completed — plus the synced ledger row (when the in-memory flip landed,
    /// for the rich 200 body) and a failure reason (surfaced as a non-2xx when the
    /// mint could not be completed). This replaces the old "return synced or null"
    /// shape that conflated "row not warm locally" (fine — the durable SoT answers)
    /// with "delivery not minted" (NOT fine — the chain is broken), which is exactly
    /// what let the silent-degrade hide a missing delivery.
    /// </summary>
    private readonly struct AcceptOrchestrationOutcome
    {
        /// <summary>True when the canonical delivery row was minted/assigned.</summary>
        public bool DeliveryMinted { get; private init; }

        /// <summary>The synced request ledger row when the local flip landed; else null.</summary>
        public DeliveryRequest? SyncedRequest { get; private init; }

        /// <summary>Human-readable reason the mint could not complete; null on success.</summary>
        public string? FailureReason { get; private init; }

        public static AcceptOrchestrationOutcome MintSucceeded(DeliveryRequest? synced) => new()
        {
            DeliveryMinted = true,
            SyncedRequest = synced,
            FailureReason = null,
        };

        public static AcceptOrchestrationOutcome MintFailed(DeliveryRequest? synced, string? reason) => new()
        {
            DeliveryMinted = false,
            SyncedRequest = synced,
            FailureReason = reason,
        };

        /// <summary>
        /// The upstream accept returned 200 but with no envelope — we cannot resolve
        /// a winner to assign, so the mint cannot proceed. Treated as a mint failure.
        /// </summary>
        public static AcceptOrchestrationOutcome MissingEnvelope() => new()
        {
            DeliveryMinted = false,
            SyncedRequest = null,
            FailureReason = "Upstream accept returned no envelope; the winning jeeber and delivery could not be resolved.",
        };
    }
}
