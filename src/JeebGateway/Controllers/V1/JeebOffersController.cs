using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Financials;
using JeebGateway.Observability;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
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
/// Delegates UNCONDITIONALLY to the offer-service accept saga. GW3 / W3.5(c)
/// deleted the flag-off local accept along with the in-memory offer store that
/// drove it, so <c>FeatureFlags:UseUpstream:Offer</c> no longer selects an accept
/// path here and there is nothing left to fall back to. The caller is the
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
    /// <summary>Retired BR-10 cap: active deliveries are unlimited.</summary>
    private const int ActiveDeliveriesLimit = int.MaxValue;

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly IOfferServiceClient _offerService;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IDeliveryServiceClient _deliveryService;
    private readonly ITiersStore _tiers;
    // GW5 / W1.6-gateway: the post-accept chat step is delegated to the settler, which
    // the reconciler shares. This controller no longer holds IJeebConversationClient —
    // it composed the two-call seat/advance sequence that the settle replaces, and a
    // controller keeping a live handle on the chat client is how a second, drifting copy
    // of that sequence gets written again.
    private readonly IAcceptChatSettler _settler;
    private readonly IOfferPushNotifier _offerPush;
    private readonly IDetachedPushDispatcher _detachedPush;
    private readonly IWalletSufficiencyGuard _walletGuard;
    private readonly ICommissionCollector _commission;
    private readonly IHandoverCodeStore _handoverCodes;
    private readonly UpstreamFeatureFlags _flags;
    private readonly DeliveryClientOptions _deliveryOptions;
    // P7 (G-E): the ONE clock this controller stamps DeliveryRequestDto.ServerNow from.
    private readonly TimeProvider _clock;
    private readonly ILogger<JeebOffersController> _logger;

    public JeebOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IDeliveryServiceClient deliveryService,
        ITiersStore tiers,
        IAcceptChatSettler settler,
        IOfferPushNotifier offerPush,
        IDetachedPushDispatcher detachedPush,
        IWalletSufficiencyGuard walletGuard,
        ICommissionCollector commission,
        IHandoverCodeStore handoverCodes,
        IOptions<UpstreamFeatureFlags> flags,
        IOptions<DeliveryClientOptions> deliveryOptions,
        TimeProvider clock,
        ILogger<JeebOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _deliveryService = deliveryService;
        _tiers = tiers;
        _settler = settler;
        _offerPush = offerPush;
        _detachedPush = detachedPush;
        _walletGuard = walletGuard;
        _commission = commission;
        _handoverCodes = handoverCodes;
        _flags = flags.Value;
        _deliveryOptions = deliveryOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/offers/{id}/accept — accept an offer, closing the auction.
    ///
    /// The caller is the request-owning CLIENT awarding the delivery to one
    /// jeeber's bid.
    ///
    /// The gateway resolves <c>offerId → requestId</c> via the in-process offer
    /// routing index, then forwards to the offer-service accept saga which owns OTP
    /// mint, chat-thread open, sibling rejection, and SELECT FOR UPDATE race-safety.
    /// The upstream HTTP status is surfaced verbatim.
    ///
    /// GW3 / W3.5(c): there is no longer a second, flag-off accept path here. The
    /// local in-memory accept was deleted with the in-memory offer store it drove.
    /// </summary>
    [HttpPost("v1/offers/{id}/accept")]
    // W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
    [HttpPost("offers/{id}/accept")]
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

        // GW3 / W3.5(c): unconditional. The flag used to pick between this and a local
        // in-memory accept; that branch and the store behind it are deleted.
        return await AcceptUpstreamAsync(id, actorId, idempotencyKey, ct);
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
        // fix/offer-notpending-fullflow — resolve the offer-scoped accept route's
        // offerId → (requestId, jeeberId) pairing AUTHORITATIVELY and restart-safely.
        //
        // The submit-time IOfferRequestIndex is the fast cache for this pairing, but a
        // COLD gateway (post-restart / cross-replica with an empty in-memory index and
        // no durable index wired) previously resolved a genuinely-LIVE offer to null and
        // returned a bare 404 — which the mobile client renders as "this offer is no
        // longer available". That is a FALSE unavailability produced purely by lost
        // GATEWAY memory. ResolveOfferRoutingAsync removes that in-memory dependence: on
        // an index miss it reconciles the pairing from the AUTHORITATIVE offer-service
        // (the accepting owner's live offer lists), so a still-pending offer resolves and
        // accepts even after a bounce, while a genuinely gone/non-pending offer still
        // resolves to 404 (unknown) or is forwarded to the accept saga which returns the
        // authoritative 409/410 — real NotPending semantics are preserved.
        var routing = await ResolveOfferRoutingAsync(offerId, actorId, ct);
        if (routing is null)
            return NotFound();

        var requestId = routing.Value.RequestId;

        var winningJeeberId = routing.Value.JeeberId;
        // Retired BR-10 active-delivery cap: do not pre-count delivery-service
        // assignments here. Offer-service still owns real accept conflicts below.

        // F1 guard 2 — the winning jeeber must resolve to a wallet-holder GUID or the
        // re-check cannot run; a blank / non-GUID winner is DENIED, never forwarded.
        if (string.IsNullOrWhiteSpace(winningJeeberId)
            || !Guid.TryParse(winningJeeberId, out var winningJeeberGuid))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                WalletGuardContract.WalletHolderUnresolvedProblem());
        }

        var feeRes = await ResolveAcceptedFeeAsync(requestId, offerId, ct);
        if (feeRes.Degraded)
        {
            // A degraded fee read routes through the ONE FailMode knob, symmetric with a
            // wallet-service outage. NO auto-withdraw: insufficiency was never confirmed.
            if (_walletGuard.IsFailOpen)
            {
                _logger.LogWarning(
                    "F1 guard 2: accepted-fee lookup degraded; FailMode=fail-open, accept proceeds unchecked for offer {OfferId}.",
                    offerId);
            }
            else
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    WalletGuardContract.OfferFeeUnresolvableProblem());
            }
        }
        else if (feeRes.Fee is > 0m)
        {
            var required = WalletGuardContract.RequiredCommission(feeRes.Fee.Value);
            var guard = await _walletGuard.CheckAsync(winningJeeberGuid, required, ct);
            if (!guard.Allowed)
            {
                // An outage is NOT insufficiency: 503, and never withdraw the offer.
                if (guard.DegradedByUpstreamFailure)
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable,
                        WalletGuardContract.WalletUnavailableProblem());
                }

                await AutoWithdrawInsufficientBalanceOfferAsync(offerId, requestId, winningJeeberId, ct);

                return Conflict(new ProblemDetails
                {
                    Title = "The winning jeeber's wallet balance no longer covers the offer's commission.",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/offer-jeeber-insufficient-balance",
                    Extensions =
                    {
                        ["needed"] = guard.Required,
                        ["available"] = guard.Available,
                        ["currency"] = guard.Currency,
                    }
                });
            }
        }

        // JEBV4-83 (F5) — BR-1 self-offer guard (defense-in-depth), porting the legacy
        // route's check (OffersController.AcceptViaUpstreamAsync:553-564) so the two live
        // accept surfaces cannot diverge. BR-1 forbids a user acting as BOTH Client and
        // Jeeber on one delivery; the actor here is the request-OWNING client, so the only
        // legitimate violation is a genuine SELF-OFFER: the accepting client is also the
        // jeeber who bid THIS offer. We compare the actor against the offer's recorded
        // bidder (never request.ClientId, which trips on every valid accept). When the
        // bidder is unknown (cold reconciliation returned no jeeber id) we do NOT assert a
        // violation — the offer-service request-scoped accept guard remains the
        // authoritative owner of dual-role self-dealing, so we let the saga decide.
        if (!string.IsNullOrWhiteSpace(winningJeeberId)
            && string.Equals(winningJeeberId, actorId, StringComparison.Ordinal))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot accept your own delivery request (BR-1).",
                Detail = "A user cannot act as both Client and Jeeber on the same delivery.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/same-delivery-role-violation"
            });
        }

        // JEBV4-83 (F6) — deterministic Idempotency-Key fallback. When the client does
        // not send the header, fall back to a stable per-(actor, offer) key (matching
        // OffersController.AcceptViaUpstreamAsync:572) so a retry replays the SAME key and
        // offer-service dedupes it — instead of a fresh Guid per attempt that re-runs the
        // accept side-effects (delivery-leg assign, push fan-out, handover-code issue).
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"accept-{actorId}-{offerId}"
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

    /// <summary>
    /// fix/offer-notpending-fullflow — AUTHORITATIVE, restart-safe resolution of the
    /// offer-scoped accept route's <c>offerId → (requestId, jeeberId)</c> pairing.
    ///
    /// <para><b>Why.</b> The mobile accept route is offer-scoped
    /// (<c>POST /v1/offers/{offerId}/accept</c>) while the offer-service accept saga is
    /// request-scoped, so the gateway must recover the requestId from the offerId. The
    /// submit-time <see cref="IOfferRequestIndex"/> is the fast in-process (or, when
    /// state-service is wired, durable) cache for that pairing, but on a COLD gateway
    /// — empty in-memory index after a restart, or a replica that never saw the submit,
    /// with no durable index — a genuinely LIVE offer resolved to <c>null</c> and the
    /// accept returned a bare 404. The mobile client surfaces that as "this offer is no
    /// longer available": a FALSE unavailability caused solely by lost GATEWAY memory,
    /// even though the offer is perfectly pending in offer-service.</para>
    ///
    /// <para><b>What.</b> On an index MISS this reconciles the pairing from the
    /// AUTHORITATIVE offer-service instead of trusting gateway memory. The accept caller
    /// is the request-OWNING client, so the gateway enumerates that client's own still-
    /// open auctions (<see cref="RequestStatus.Pending"/>/<see cref="RequestStatus.Matched"/>)
    /// from its request read-model and, for each, reads the owner-scoped offer list
    /// (<c>GET /api/v1/requests/{id}/offers</c> — the only authoritative offer read
    /// offer-service exposes; there is deliberately no get-offer-by-id route). The offer
    /// whose id matches yields its requestId and bidder id straight from offer-service
    /// LIVE data; the pairing is re-recorded into the index so the next accept/edit is
    /// fast again (and, with the durable index wired, bounce-survivable).</para>
    ///
    /// <para><b>Stateless-correct.</b> The availability truth — does this live offer
    /// exist, and under which request/jeeber — is derived from offer-service on every
    /// cold path, never from gateway memory. The gateway performs ROUTING only and
    /// re-derives no auction rule: a matched offer is always forwarded to the accept
    /// saga, which is the sole authority for accepted/withdrawn/expired (→ 409/410), so
    /// a genuinely non-pending offer still correctly rejects. An offer that is truly
    /// gone appears in no owner list → a correct 404. DEGRADE-DON'T-FAIL: every
    /// offer-service read here is best-effort (the client returns an empty list on any
    /// blip and the request-list is bounded by the owner's open auctions), so a fault
    /// degrades to the pre-fix "unresolved → 404" contract, never a 5xx.</para>
    /// </summary>
    private async Task<(string RequestId, string? JeeberId)?> ResolveOfferRoutingAsync(
        string offerId, string actorId, CancellationToken ct)
    {
        // 1) Fast path: the submit-time routing index (in-memory, or the durable
        //    write-through decorator when state-service is wired).
        var indexedRequestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (!string.IsNullOrWhiteSpace(indexedRequestId))
        {
            return (indexedRequestId, _offerRequestIndex.ResolveJeeberId(offerId));
        }

        // 2) Cold path: authoritative reconciliation from offer-service. NEVER read an
        //    index miss as "offer gone" — the offer may be live and the index simply
        //    cold. Enumerate the accepting owner's still-open auctions and find the
        //    offer by id in the owner-scoped offer-service list.
        IReadOnlyList<DeliveryRequest> ownRequests;
        try
        {
            ownRequests = await _requests.ListForClientAsync(actorId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cold offer-routing reconciliation for offer {OfferId}: listing owner {ActorId}'s "
                + "requests failed; falling back to the unresolved (404) contract.", offerId, actorId);
            return null;
        }

        foreach (var request in ownRequests)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Id))
            {
                continue;
            }

            // Only a pre-acceptance auction (pending/matched) can still hold the offer
            // being accepted; skip terminal / in-flight rows to bound the upstream fan-out.
            if (!string.Equals(request.Status, RequestStatus.Pending, StringComparison.Ordinal)
                && !string.Equals(request.Status, RequestStatus.Matched, StringComparison.Ordinal))
            {
                continue;
            }

            // Owner-scoped read: actorId IS the request owner (offer-service 403s otherwise);
            // the client degrades a blip to an empty list, so this never throws a 5xx.
            var offers = await _offerService.ListForRequestAsync(actorId, request.Id, ct);
            var match = offers.FirstOrDefault(
                o => string.Equals(o.Id, offerId, StringComparison.Ordinal));
            if (match is null)
            {
                continue;
            }

            var resolvedJeeberId = string.IsNullOrWhiteSpace(match.JeeberId) ? null : match.JeeberId;

            // Re-hydrate the routing index from the authoritative pairing so the next
            // accept/edit of this offer resolves on the fast path (and survives the next
            // bounce when the durable index is wired). Structural routing fact only.
            _offerRequestIndex.Record(offerId, request.Id, resolvedJeeberId);

            _logger.LogInformation(
                "Cold offer-routing reconciliation for offer {OfferId}: recovered request {RequestId} "
                + "(jeeber {JeeberId}) from offer-service; index re-hydrated — no false unavailability.",
                offerId, request.Id, resolvedJeeberId);

            return (request.Id, resolvedJeeberId);
        }

        // Unknown to offer-service across all of the owner's open auctions: the offer
        // genuinely is not an acceptable pending offer for this owner → 404 (authoritative,
        // not a memory-loss false negative).
        return null;
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
        // now-deleted local accept path did via TryAcceptByJeeberAsync (GW3 / W3.5(c)).
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

        // fix/client-visibility (run-22 P1) — SNAPSHOT the accepted offer's fee onto the
        // local row. The delivery-read `amount` enrichment is otherwise re-resolved from
        // the offers store on EVERY read, which (a) is owner-scoped on the upstream wire
        // (offer-service 403s the assigned jeeber, who then never sees the agreed fee)
        // and (b) can stop matching once the offer's upstream state collapses after
        // completion — producing the $0.00 receipt. The acceptor here IS the request
        // owner, so this list read is authorized; the receipt later reads the snapshot.
        // DEGRADE-DON'T-FAIL: a fee-resolution miss is logged, never a 5xx.
        decimal? acceptedFee = null;
        try
        {
            // A degraded read yields a null Fee here, so the snapshot is simply skipped —
            // never a 5xx on this path (the saga already committed upstream).
            acceptedFee = (await ResolveAcceptedFeeAsync(requestId, offerId, ct)).Fee;
            if (acceptedFee is > 0m
                && await _requests.TrySetAcceptedFeeAsync(requestId, acceptedFee.Value, ct))
            {
                req = await _requests.GetAsync(requestId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept fee snapshot for request {RequestId} (offer {OfferId}) failed; accept "
                + "stays 200 — the receipt amount falls back to the live offers lookup.",
                requestId, offerId);
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

        // O1 (owner amendment 2026-08-16): "the wallet only drain when he make an offer and it is
        // accepted". This is that drain — the single accept convergence point, post-commit.
        //
        // DEGRADE-DON'T-FAIL, like every other step in this block: the saga has already committed a
        // winner, so an uncollectable fee can never turn a closed auction into a 5xx. The collector
        // never throws; its outcome is counted and logged. Awaited, not detached, because money must
        // resolve inside the request that caused it rather than after the response.
        var billableFee = acceptedFee ?? req?.AcceptedFee;
        if (!string.IsNullOrWhiteSpace(winningJeeberId) && billableFee is > 0m)
        {
            try
            {
                await _commission.CollectOnAcceptAsync(
                    new CommissionCollectionCommand(requestId, winningJeeberId, billableFee.Value), ct);
            }
            catch (Exception ex)
            {
                // The collector is contracted never to throw; this is the belt for the day that
                // contract breaks, because a closed auction must never surface as a 5xx.
                BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
                _logger.LogError(ex,
                    "commission.accept.threw requestId={RequestId} jeeberId={JeeberId}; accept stays "
                    + "200 and the fee is UNCOLLECTED.", requestId, winningJeeberId);
            }
        }
        else
        {
            _logger.LogWarning(
                "commission.accept.no_basis requestId={RequestId} offerId={OfferId} jeeberId={JeeberId} "
                + "fee={Fee}; the accept stands but there is nothing to bill against.",
                requestId, offerId, winningJeeberId, billableFee);
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
        //
        // DETACHED, not awaited (JEBV4-281's lesson, applied to the seat that needed it most).
        // The per-recipient push budget is PushSendBudget.PerRecipient because that is what a
        // push to a recipient who actually owns a device costs — 2.53-3.97s measured, so the
        // old 2s cap aborted every healthy send. But winner + N losers awaited in front of
        // this response at that budget is 10s x (1+N), past the mobile client's receive
        // timeout, and the accept has ALREADY COMMITTED: the customer would be told "No
        // internet connection" about an auction they successfully closed. Raising the cap is
        // only safe behind the response, so the fan-out moves behind it.
        DispatchAcceptLifecyclePushes(requestId, offerId, winningJeeberId, result.Envelope?.RejectedOfferIds);

        // Gap G4 (run-24 CHECK C) — mint the CUSTOMER's in-app handover code at accept
        // and ride it ONLY on this owner's accept response as `handoverCode`. The
        // acceptor IS the request owner (offer-service returns NotOwner -> 403 before
        // ever reaching Accepted here), so this is owner-scoped by construction — the code never reaches the jeeber or any
        // non-owner. The gateway matches it at handover (verify-precedence in
        // DeliveriesController). DEGRADE-DON'T-FAIL: a cache blip yields a null code
        // (the SMS/one-time-password handover still works), never a 5xx.
        var handoverCode = await IssueHandoverCodeSafeAsync(requestId, ct);

        if (req is not null)
            return Ok(ToRequestDto(req, _clock.GetUtcNow(), handoverCode));

        // Request not in local store (delivery-service is the SoT).
        // Return a minimal acknowledgement so the client knows acceptance succeeded.
        return Ok(new
        {
            requestId,
            acceptedOfferId = result.Envelope?.AcceptedOfferId,
            jeeberId = result.Envelope?.JeeberId,
            status = "accepted",
            handoverCode
        });
    }

    /// <summary>
    /// Gap G4 (run-24 CHECK C): mint (or return the already-issued) in-app handover
    /// code for the just-accepted delivery. Owner-scoped by construction — this runs
    /// only after an accept the caller was authorised to make, and the code rides ONLY
    /// that owner's accept response. DEGRADE-DON'T-FAIL: a cache blip returns null (the
    /// code is simply absent from the response; the SMS / one-time-password handover
    /// path is unaffected), never a 5xx. The raw code is NEVER logged — only the
    /// deliveryId and the exception type on the failure path.
    /// </summary>
    private async Task<string?> IssueHandoverCodeSafeAsync(string deliveryId, CancellationToken ct)
    {
        try
        {
            return await _handoverCodes.IssueAsync(deliveryId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-accept handover-code issue for delivery {DeliveryId} failed; accept stays 200 "
                + "— the customer's in-app code is omitted (the SMS handover code still works).",
                deliveryId);
            return null;
        }
    }

    /// <summary>
    /// sprint-009 Lane E — best-effort accept-lifecycle push fan-out. Sends exactly one
    /// <c>jeeb.offer_accepted</c> push to the winning jeeber and one
    /// <c>jeeb.offer_rejected</c> push per rejected sibling (resolving each losing bidder
    /// from the offer routing index via <see cref="IOfferRequestIndex.ResolveJeeberId"/>).
    ///
    /// <para>Returns <c>void</c> and dispatches through <see cref="IDetachedPushDispatcher"/>:
    /// the sends run BEHIND the accept response. That is both halves of degrade-don't-fail at
    /// once — a bug in the fan-out cannot flip the committed 200 into a 5xx, and a slow push
    /// cannot hold the 200 past the client's receive timeout. The belt-and-braces try/catch
    /// that used to live here moved into the dispatcher, which is the only thing left that can
    /// observe a fault on this path.</para>
    /// </summary>
    private void DispatchAcceptLifecyclePushes(
        string requestId,
        string acceptedOfferId,
        string? winningJeeberId,
        IReadOnlyList<string>? rejectedOfferIds)
    {
        // Resolve the losing bidders from the routing index learned at submit time, on the
        // REQUEST thread: IOfferRequestIndex is a singleton and the lookup is in-memory, so
        // doing it here costs nothing and lets the detached budget be sized from a recipient
        // count that is already known. A null result (offer unknown to this instance /
        // recorded without a jeeber id) means we cannot address the push — skip, never guess.
        var losers = new List<(string JeeberId, string OfferId)>();
        if (rejectedOfferIds is not null)
        {
            foreach (var rejectedOfferId in rejectedOfferIds)
            {
                if (string.IsNullOrWhiteSpace(rejectedOfferId))
                    continue;

                var loserJeeberId = _offerRequestIndex.ResolveJeeberId(rejectedOfferId);
                if (string.IsNullOrWhiteSpace(loserJeeberId))
                    continue;

                losers.Add((loserJeeberId, rejectedOfferId));
            }
        }

        var winner = string.IsNullOrWhiteSpace(winningJeeberId) ? null : winningJeeberId;
        var recipientCount = (winner is null ? 0 : 1) + losers.Count;
        if (recipientCount == 0)
        {
            return;
        }

        _detachedPush.Dispatch(
            "offer.accept_lifecycle", recipientCount, correlationId: requestId,
            work: async (sp, token) =>
            {
                var push = sp.GetRequiredService<IOfferPushNotifier>();

                if (winner is not null)
                {
                    await push.NotifyOfferAcceptedAsync(winner, requestId, acceptedOfferId, token);
                }

                foreach (var (loserJeeberId, rejectedOfferId) in losers)
                {
                    await push.NotifyOfferLostAsync(loserJeeberId, requestId, rejectedOfferId, token);
                }
            });
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
    /// chat blip / disabled flag / lookup miss is logged and swallowed — never a 5xx.
    /// Gated on the Chat upstream flag, mirroring the offer-submit seat.</para>
    ///
    /// <para><b>GW5 / W1.6-gateway — WHAT CHANGED AND WHY.</b> The seat and the phase
    /// advance used to be TWO independent chat-service requests issued from inside this
    /// post-commit block. When the first landed and the second did not, the winner sat in
    /// a conversation still in its pre-settlement phase with every losing bidder still
    /// active — and this method's own catch logged <i>"may read 403 on chat until
    /// reconciled"</i> while nothing reconciled. Accept is the money-committing step and
    /// chat is the only coordination channel a cash handover has, so that half-state is
    /// real damage, not a cosmetic lag.</para>
    ///
    /// <para>Failing loud here cannot fix it: this code runs AFTER the saga has
    /// committed, so there is nothing left to abort. The fix is therefore two-part and
    /// neither part is this method's own error handling — (1) ONE additive
    /// <c>POST /api/conversations/{id}/settle</c> call, which removes the window between
    /// the two writes, and (2) <see cref="JeebGateway.Conversations.AcceptChatSettleReconciler"/>,
    /// which re-derives lost attempts from the durable request row and heals them. Both
    /// live in <see cref="IAcceptChatSettler"/> because the reconciler must perform the
    /// identical step; a second copy of a money-adjacent saga step is a second place to
    /// drift.</para>
    /// </summary>
    private async Task EnsureConversationAndSeatWinnerAsync(
        DeliveryRequest? request, string? winningJeeberId, CancellationToken ct)
    {
        try
        {
            // Flag gating, the resolve-or-create, the projection stamp and the ONE
            // seat-and-settle call all live in the settler — see IAcceptChatSettler.
            // It THROWS on a chat-service fault; deciding what that means is this
            // caller's job, and here it means "log and stay 200".
            await _settler.SettleAsync(request, winningJeeberId, ct);
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL, unchanged: the saga already committed upstream, so this
            // must never turn a successful accept into a 5xx.
            //
            // The log line no longer promises a reconciliation that does not exist. The
            // counter is the part that matters — a warning nobody aggregates is how this
            // defect stayed invisible. Compare chat.accept_settle.failures against
            // chat.accept_settle.settled; a zero on its own proves nothing.
            JeebGateway.Conversations.ChatSettleTelemetry.Failures.Add(1);
            _logger.LogWarning(ex,
                "Post-accept conversation ensure/settle for request {RequestId} failed; accept "
                + "stays 200. Jeeber {JeeberId} reads 403 on chat until AcceptChatSettleReconciler "
                + "heals it (candidate is durable: the request row already carries the assignment).",
                request?.Id, winningJeeberId);
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
            var resolvedTierId = await _tiers.ResolveAsync(request.TierId!, ct);
            if (resolvedTierId is null)
            {
                _logger.LogWarning(
                    "Post-accept delivery-leg sync for request {RequestId}: tier {TierId} no longer resolves; "
                    + "skipping the assignment mirror (accept stays 200).",
                    request.Id, request.TierId);
                return;
            }

            await _deliveryService.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
            {
                Id = request.Id,
                TenantId = _deliveryOptions.TenantId,
                ClientId = request.ClientId,
                JeeberId = winningJeeberId,
                TierId = resolvedTierId,
                PickupLat = request.PickupLocation.Lat,
                PickupLng = request.PickupLocation.Lng,
            }, ct);

            // JEBV4-300 — DURABLE-BEFORE-RETURN. The upsert above is fire-and-forget
            // against a possibly read-replica-lagged delivery-service; until its row
            // carries jeeber_id its authorise() 403s BOTH parties, so a PATCH /status
            // fired seconds after accept races the mirror. Confirm the assignment is
            // visible on the canonical row before the accept returns. NEVER throws on a
            // non-confirming read — the outer swallow keeps a committed accept at 200 and
            // DeliveriesController's PATCH-status re-mirror (leg b) self-heals the residual.
            await ConfirmDeliveryAssignmentVisibleAsync(request.Id, winningJeeberId, ct);
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
    /// until the row's <c>jeeber_id</c> equals the winner. NEVER throws on a non-confirming
    /// read: the caller's swallow keeps a committed accept at 200 and leg (b) self-heals.
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

    // -----------------------------------------------------------------------
    // Accept-response helpers  (upstream path — see AcceptUpstreamAsync)
    //
    // This banner used to read "In-memory accept path (legacy / test-only; flag
    // off)". It was accurate until GW3 deleted the local accept helper it headed
    // (the tombstone below records that deletion). What is left under it is
    // ResolveAcceptedFeeAsync, which BuildAcceptedResponseAsync calls on the
    // UPSTREAM accept path, and the response mapper. A section header that labels
    // live upstream code "test-only, flag off" is the kind of note that stops the
    // next reader from looking.
    // -----------------------------------------------------------------------

    /// <summary>Resolves the accepted offer's fee via the owner-scoped list read (caller IS the owner).
    /// <c>Failed</c> = the READ degraded (fail-mode at the guard); <c>Ok(null)</c> = 2xx with no fee.</summary>
    private async Task<FeeResolution> ResolveAcceptedFeeAsync(string requestId, string offerId, CancellationToken ct)
    {
        var res = await _offers.TryListForRequestAsync(requestId, ct);
        if (res.Degraded) return FeeResolution.Failed;
        return FeeResolution.Ok(
            res.Items.FirstOrDefault(o => string.Equals(o.Id, offerId, StringComparison.Ordinal))?.Fee);
    }

    /// <summary>F1 guard 2, best-effort: withdraw the unaffordable offer + reuse the lost
    /// push. Correction 7: NotPending (replay of accepted offer) is swallowed.</summary>
    private async Task AutoWithdrawInsufficientBalanceOfferAsync(
        string offerId, string requestId, string jeeberId, CancellationToken ct)
    {
        try
        {
            var outcome = await _offers.TryWithdrawAsync(offerId, requestId, jeeberId, _clock.GetUtcNow(), ct);
            if (outcome != WithdrawOfferOutcome.Withdrawn)
            {
                return;
            }

            _detachedPush.Dispatch(
                "offer.insufficient_balance", recipientCount: 1, correlationId: requestId,
                work: (sp, token) => sp.GetRequiredService<IOfferPushNotifier>()
                    .NotifyOfferLostAsync(jeeberId, requestId, offerId, token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "F1 guard 2 auto-withdraw for offer {OfferId} (insufficient balance) failed; "
                + "the 409 already returned to the caller, the stale offer may resurface until reconciled.",
                offerId);
        }
    }

    // GW3 / W3.5(c): the local in-memory accept helper was deleted here.
    //
    // It was the flag-OFF half of Accept above: a ~95-line local re-implementation of
    // the auction close (ownership guard, already-accepted 409 with the winner,
    // TryAcceptByJeeberAsync, supersede, accepted-fee snapshot, handover-code mint).
    // Every deployed overlay sets FeatureFlags:UseUpstream:Offer = true, so the branch
    // was unreachable in every environment that exists, and the offer ledger it drove
    // was the in-memory store this batch also removed. GW5 landed the gateway half of
    // the upstream accept saga (seat-and-settle, reconcile-on-failure), so
    // AcceptUpstreamAsync is now the only accept path and Accept forwards to it
    // unconditionally.
    //
    // Do NOT reinstate a local accept as a "fallback". Two implementations of one
    // auction close is how single-winner races get lost; offer-service owns the
    // SELECT FOR UPDATE.

    // P7 (G-E): ServerNow is required on the DTO — ONE clock read per response.
    // The accept surface is post-acceptance, so the offer-deadline fields stay null
    // (IsPreAcceptance is false for an accepted row anyway).
    private static DeliveryRequestDto ToRequestDto(
        DeliveryRequest r, DateTimeOffset serverNow, string? handoverCode = null) => new()
    {
        ServerNow = serverNow,
        ExpiredAt = r.ExpiredAt,
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
        // Gap G4: null on every projection EXCEPT the owner's accept response, where the
        // handler passes the freshly-minted code. Omitted from JSON when null.
        HandoverCode = handoverCode,
    };
}
