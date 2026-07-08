using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.Availability;

/// <summary>
/// Thin-BFF adapter that satisfies <see cref="IPendingOffersStore"/> by
/// proxying the real offer-service (Elixir/Phoenix, host port 10063) through
/// <see cref="IOfferServiceClient"/>. Selected over
/// <see cref="InMemoryPendingOffersStore"/> in <c>Program.cs</c> when
/// <c>FeatureFlags:UseUpstream:Offer</c> is true; the in-memory store remains
/// the flag-OFF fallback (store deletion is a tracked fast-follow, not this PR).
///
/// Contract impedance handled here:
///
/// <list type="bullet">
///   <item><b>Money units.</b> The gateway's <see cref="PendingOffer.Fee"/> is
///     decimal dollars; offer-service stores integer <c>fee_cents</c>. We map
///     dollars → cents on the way out and back on the way in.</item>
///   <item><b>Status vocabulary.</b> offer-service emits
///     submitted/edited/withdrawn/accepted/rejected/expired/pending; the
///     gateway's three-state model is pending/accepted/withdrawn. We collapse
///     the live states (submitted/edited/pending) to <c>pending</c>, keep
///     <c>accepted</c>, and treat every other terminal state as
///     <c>withdrawn</c>.</item>
///   <item><b>Conflict codes.</b> offer-service returns HTTP 409 on submit.
///     The gateway can preserve specific duplicate/request-not-open exceptions
///     only when upstream emits typed codes; today's real wire emits generic
///     <c>conflict</c> for those cases, so it remains a generic submit 409
///     instead of being rendered as a retired offer-count cap.</item>
///   <item><b>Acting user.</b> offer-service authorizes on a gateway-injected
///     <c>x-user-id</c> header. The store contract already threads the acting
///     <c>jeeberId</c> into every write, so we forward it directly.</item>
/// </list>
///
/// <para><b>Out of scope for this wire (tracked fast-follow).</b> offer-service
/// currently exposes only the four write/auction routes
/// (submit / edit / withdraw / accept) — there is no "get offer by id",
/// "list offers for request", or "withdraw all offers for a jeeber" route.
/// <see cref="GetAsync"/>, <see cref="ListForRequestAsync"/> and
/// <see cref="WithdrawForJeeberAsync"/> therefore throw
/// <see cref="NotSupportedException"/> under the upstream wire; the
/// auto-offline sweeper and the offer-accept lookup path stay on the in-memory
/// store until offer-service grows those read routes. The flag is OFF in
/// non-production so the existing fixtures (which exercise those paths) stay
/// green.</para>
/// </summary>
public sealed class UpstreamPendingOffersStore : IPendingOffersStore
{
    /// <summary>
    /// offer-service's own floor (<c>validate_number(:fee_cents, greater_than_or_equal_to: 100)</c>)
    /// and the gateway's $1 minimum. Used to surface clear duplicate/conflict
    /// translation; fee validation itself stays in the controller.
    /// </summary>
    private const long MinimumFeeCents = 100;

    private readonly IOfferServiceClient _client;
    private readonly IHttpContextAccessor? _httpContext;
    private readonly IOfferRequestIndex? _offerIndex;
    private readonly IRequestsStore? _requests;

    /// <param name="httpContext">
    /// Optional so the existing single-arg construction in the write/conflict unit tests compiles
    /// unchanged (those paths never read the caller identity). The production DI factory in
    /// <c>Program.cs</c> always injects the real accessor, which <see cref="ListForRequestAsync"/>
    /// needs to forward the owner's <c>x-user-id</c> to offer-service.
    /// </param>
    /// <param name="offerIndex">
    /// Optional (fix/offer-visibility): the submit-time offerId → (requestId, jeeberId) routing
    /// index. Needed only by <see cref="ListForJeeberAsync"/> to recover the jeeber's own offers
    /// (offer-service exposes no jeeber-scoped list route); when absent that read degrades to an
    /// empty list, the pre-fix behaviour.
    /// </param>
    /// <param name="requests">
    /// Optional (fix/offer-visibility): the gateway request read-model, used by
    /// <see cref="ListForJeeberAsync"/> to resolve each bid's request OWNER (offer-service's
    /// request-scoped list authorizes on <c>x-user-id == owner</c>).
    /// </param>
    public UpstreamPendingOffersStore(
        IOfferServiceClient client,
        IHttpContextAccessor? httpContext = null,
        IOfferRequestIndex? offerIndex = null,
        IRequestsStore? requests = null)
    {
        _client = client;
        _httpContext = httpContext;
        _offerIndex = offerIndex;
        _requests = requests;
    }

    public async Task<PendingOffer> TrySubmitAsync(
        string requestId,
        string jeeberId,
        decimal fee,
        int etaMinutes,
        string? note,
        int maxPerRequest,
        DateTimeOffset at,
        CancellationToken ct,
        string? clientId = null)
    {
        var feeCents = ToCents(fee);

        try
        {
            return await SubmitOnceAsync(requestId, jeeberId, feeCents, etaMinutes, note, maxPerRequest, ct);
        }
        catch (OfferRequestNotMirroredException) when (clientId is { Length: > 0 })
        {
            // GW-1 self-heal. offer-service 404'd because the request row was
            // never mirrored. Mirror it (idempotent OS-1) on the request
            // creator's behalf, then retry the submit EXACTLY ONCE. If the
            // retry 404s again, the not-found is genuine (not a missing mirror)
            // and is allowed to surface as a 404, so we never loop.
            await _client.MirrorRequestAsync(jeeberId, requestId, clientId!, ct);
            return await SubmitOnceAsync(requestId, jeeberId, feeCents, etaMinutes, note, maxPerRequest, ct);
        }
    }

    /// <summary>
    /// One submit attempt with the 409 → duplicate/request-not-open/generic-conflict translation. The 404
    /// (<see cref="OfferRequestNotMirroredException"/>) and 422/400
    /// (<see cref="OfferUpstreamValidationException"/>) cases are surfaced by the
    /// client and handled by the caller (mirror-retry / ProblemDetails mapping).
    /// </summary>
    private async Task<PendingOffer> SubmitOnceAsync(
        string requestId,
        string jeeberId,
        long feeCents,
        int etaMinutes,
        string? note,
        int maxPerRequest,
        CancellationToken ct)
    {
        try
        {
            var wire = await _client.SubmitAsync(jeeberId, requestId, feeCents, etaMinutes, note, ct);
            return ToPendingOffer(wire);
        }
        catch (OfferUpstreamConflictException ex)
        {
            // offer-service reuses one 409 surface for multiple distinct conflicts:
            //   (1) typed "you already offered"  -> DuplicateOfferException (409 offer-already-exists)
            //   (2) typed "request not open"     -> RequestNotOpenForOffersException (409 request-not-open-for-offers)
            //   (3) generic/unknown conflict     -> OfferSubmitConflictException (409 offer-submit-conflict)
            // The retired 20-offer cap must not be inferred from an unknown upstream code.
            // Fidelity gap: current offer-service submit renders request_not_open and
            // already_submitted as generic code=conflict, so the specific branches are
            // unreachable until offer-service emits typed error codes.
            // offer-service submit has no count cap: unique (request_id, jeeber_id) only; 409s are state conflicts; edit cap is 422.
            if (IsDuplicateCode(ex.UpstreamCode))
            {
                // The upstream owns the existing offer id; we do not have it
                // here, so report the request-scoped duplicate without it.
                throw new DuplicateOfferException(requestId, jeeberId, existingOfferId: "(upstream)");
            }

            if (IsRequestNotOpenCode(ex.UpstreamCode))
            {
                throw new RequestNotOpenForOffersException(requestId, ex.UpstreamCode);
            }

            throw new OfferSubmitConflictException(requestId, ex.UpstreamCode);
        }
    }

    public async Task<WithdrawOfferOutcome> TryWithdrawAsync(
        string offerId,
        string requestId,
        string jeeberId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var result = await _client.WithdrawAsync(jeeberId, requestId, offerId, ct);
        return result switch
        {
            OfferWithdrawResult.Withdrawn => WithdrawOfferOutcome.Withdrawn,
            OfferWithdrawResult.NotFound => WithdrawOfferOutcome.NotFound,
            OfferWithdrawResult.NotOwned => WithdrawOfferOutcome.NotOwned,
            OfferWithdrawResult.NotPending => WithdrawOfferOutcome.NotPending,
            _ => WithdrawOfferOutcome.NotFound
        };
    }

    public Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct)
    {
        // The in-memory contract's AcceptAsync takes only the offer id because
        // it can resolve the owning jeeber/request from local state. Upstream
        // accept is keyed by (request, offer) + x-user-id + a mandatory
        // Idempotency-Key, none of which this signature carries — the gateway's
        // own accept orchestration (OffersController) already resolves and
        // transitions the request before flipping offer state. Wiring the full
        // upstream accept (which itself closes the auction: OTP, chat thread,
        // request transition) would double-run that orchestration. The accept
        // path is therefore explicitly out of scope for THIS thin-BFF wire and
        // tracked as a fast-follow once OffersController is migrated to call the
        // upstream accept envelope directly.
        throw new NotSupportedException(
            "offer-service accept is wired through OffersController's own auction-close orchestration, " +
            "not the IPendingOffersStore.AcceptAsync seam. Keep FeatureFlags:UseUpstream:Offer OFF for the " +
            "accept path until OffersController is migrated to IOfferServiceClient.AcceptAsync (tracked fast-follow).");
    }

    public Task<AcceptOfferOutcome> AcceptWithSupersedeAsync(
        string offerId, DateTimeOffset at, CancellationToken ct)
        => throw new NotSupportedException(
            "offer-service owns the accept-and-supersede auction-close (SELECT FOR UPDATE single-winner + " +
            "sibling rejection) via OffersController's upstream orchestration (IOfferServiceClient.AcceptWithStatusAsync), " +
            "NOT the IPendingOffersStore seam. The supersede-aware in-memory accept is the flag-OFF path only.");

    public Task<EditOfferOutcome> TryEditAsync(
        string offerId,
        string requestId,
        string jeeberId,
        decimal? fee,
        int? etaMinutes,
        string? note,
        int maxEdits,
        DateTimeOffset at,
        CancellationToken ct)
        => throw new NotSupportedException(
            "offer-service owns the edit rule + the 2-edit cap via OffersController's upstream forward " +
            "(IOfferServiceClient.EditAsync with max_edits); the in-memory TryEditAsync is the flag-OFF path only.");

    public Task<PendingOffer?> GetAsync(string offerId, CancellationToken ct)
        => throw new NotSupportedException(
            "offer-service exposes no get-offer-by-id route; the offer-accept lookup path stays on the " +
            "in-memory store until offer-service grows GET /api/v1/offers/{id} (tracked fast-follow).");

    /// <summary>
    /// BUG-3 fix (customer offers-read 500). offer-service GREW
    /// <c>GET /api/v1/requests/{id}/offers</c> after the original "tracked fast-follow" comment was
    /// written, so the old unconditional <see cref="NotSupportedException"/> surfaced as a hard 500 on
    /// the live upstream wire (<c>UseUpstream:Offer=true</c>) — the customer could never list/accept the
    /// jeeber's offer (Core Flow step 4). We now proxy that owner-scoped route: offer-service authorizes
    /// on <c>x-user-id == owner</c> (else 403), and the owner is the in-request authenticated caller
    /// whose ownership the controller (<c>JeebRequestsController.ListOffers/ListOffersFlat</c> and
    /// <c>JeebOrdersListController</c>) has ALREADY verified before reaching this store. Money (cents→
    /// dollars) and the status vocabulary are mapped via <see cref="ToPendingOffer"/>; the
    /// degrade-don't-fail (a blip → empty list, never a 500) lives in the client.
    /// </summary>
    public async Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(string requestId, CancellationToken ct)
    {
        var actingUserId = ResolveOwnerId();
        if (string.IsNullOrWhiteSpace(actingUserId))
        {
            // No in-request identity to authorize the upstream read (not expected on the controller
            // path, which is always authenticated). Return empty rather than 500 — the offers-read
            // must never crash the customer's accept sheet.
            return Array.Empty<PendingOffer>();
        }

        var wires = await _client.ListForRequestAsync(actingUserId!, requestId, ct);
        return wires
            .Where(w => !string.IsNullOrWhiteSpace(w.Id))
            .Select(ToPendingOffer)
            .ToList();
    }

    /// <summary>
    /// The in-request authenticated caller's canonical id (JWT <c>sub</c>/<c>sid</c>, or the edge-
    /// injected <c>X-User-Id</c>), resolved exactly as the controllers do via
    /// <see cref="UserIdentity"/>. On every caller of <see cref="ListForRequestAsync"/> this is the
    /// request owner (ownership is controller-verified first), which is the identity offer-service's
    /// owner-scoped list route requires. <c>null</c> when there is no HTTP context / no identity.
    /// </summary>
    private string? ResolveOwnerId()
    {
        var ctx = _httpContext?.HttpContext;
        if (ctx is null) return null;
        return UserIdentity.TryGetUserId(ctx, out var userId, out _) ? userId : null;
    }

    /// <summary>
    /// fix/offer-visibility (run-23 CHECK C) — the jeeber's own offers, INCLUDING terminal
    /// ones, on the upstream wire.
    ///
    /// <para><b>Why composed, not proxied.</b> offer-service exposes NO jeeber-scoped list
    /// route (its router carries only submit/edit/withdraw/accept/reject plus the
    /// owner-scoped <c>GET /api/v1/requests/{id}/offers</c>), so the gateway's
    /// <c>GET /api/v1/jeebers/{id}/offers</c> call 404s and degrade-don't-fail collapsed the
    /// jeeber's list to <c>[]</c> the moment anything was asked of it — the run-23 defect.
    /// The BFF instead recovers the jeeber's offer ids from the submit-time routing index,
    /// groups them by request, and reads each request's offer list through the EXISTING
    /// owner-scoped route, forwarding the request OWNER's id (resolved from the gateway's
    /// own request read-model) as <c>x-user-id</c>. Authorization is enforced at the
    /// gateway: the caller's self-scope is checked by the controller, and only rows whose
    /// bidder is <paramref name="jeeberId"/> are returned — a jeeber can never see another
    /// bidder's offers through this read.</para>
    ///
    /// <para><b>Honest terminal status.</b> The customer-facing <see cref="MapStatus"/>
    /// folds every non-accept terminal state to <c>withdrawn</c>; the jeeber's OWN view
    /// must not lie about why the bid ended, so <see cref="MapOwnStatus"/> keeps the
    /// distinction: lost-the-auction / expired → <c>superseded</c> ("not selected", the
    /// state mobile already renders), self-retracted → <c>withdrawn</c>.</para>
    ///
    /// <para>DEGRADE-DON'T-FAIL: a missing index/read-model or an upstream blip yields
    /// whatever rows could be resolved (possibly none), never a 5xx. Within-instance
    /// completeness matches the routing index's documented contract (a bounce re-learns
    /// pairings as offers are submitted).</para>
    /// </summary>
    public async Task<IReadOnlyList<PendingOffer>> ListForJeeberAsync(
        string jeeberId, CancellationToken ct)
    {
        if (_offerIndex is null || _requests is null || string.IsNullOrWhiteSpace(jeeberId))
        {
            return Array.Empty<PendingOffer>();
        }

        var offerIds = _offerIndex.ListOfferIdsForJeeber(jeeberId);
        if (offerIds.Count == 0)
        {
            return Array.Empty<PendingOffer>();
        }

        var requestIds = offerIds
            .Select(id => _offerIndex.ResolveRequestId(id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal);

        var mine = new List<PendingOffer>();
        foreach (var requestId in requestIds)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve the request OWNER — offer-service's request-scoped list authorizes
            // on x-user-id == owner. A request unknown to the local read-model cannot be
            // read upstream; skip it (never guess an identity).
            var request = await _requests.GetAsync(requestId, ct);
            if (request is null || string.IsNullOrWhiteSpace(request.ClientId))
            {
                continue;
            }

            // The client degrades any non-2xx / transport blip to an empty list.
            var wires = await _client.ListForRequestAsync(request.ClientId, requestId, ct);
            foreach (var wire in wires)
            {
                if (string.IsNullOrWhiteSpace(wire.Id)) continue;
                if (!string.Equals(wire.JeeberId, jeeberId, StringComparison.Ordinal)) continue;

                mine.Add(ToOwnPendingOffer(wire));
            }
        }

        return mine
            .OrderByDescending(o => o.CreatedAt)
            .ToList();
    }

    public Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct)
        => throw new NotSupportedException(
            "offer-service exposes no bulk withdraw-for-jeeber route; the auto-offline sweeper stays on the " +
            "in-memory store until offer-service grows a per-jeeber withdrawal route (tracked fast-follow).");

    // --- mapping helpers ---

    /// <summary>Dollars → integer cents, rounded half-away-from-zero.</summary>
    private static long ToCents(decimal dollars)
        => (long)decimal.Round(dollars * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Integer cents → decimal dollars.</summary>
    private static decimal ToDollars(long cents) => cents / 100m;

    private static PendingOffer ToPendingOffer(OfferWire wire) => new()
    {
        Id = wire.Id,
        RequestId = wire.RequestId,
        JeeberId = wire.JeeberId,
        Status = MapStatus(wire.Status),
        CreatedAt = wire.CreatedAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = wire.UpdatedAt,
        Fee = ToDollars(wire.FeeCents),
        EtaMinutes = wire.EtaMinutes,
        Note = wire.Note,
    };

    /// <summary>
    /// Collapse offer-service's seven-state vocabulary onto the gateway's
    /// three states. Live (submitted / edited / pending) → pending; accepted →
    /// accepted; every terminal non-accept state → withdrawn.
    /// </summary>
    private static string MapStatus(string upstream) => upstream switch
    {
        "submitted" or "edited" or "pending" => PendingOfferStatus.Pending,
        "accepted" => PendingOfferStatus.Accepted,
        _ => PendingOfferStatus.Withdrawn
    };

    /// <summary>
    /// fix/offer-visibility — <see cref="ListForJeeberAsync"/>'s wire → local projection
    /// with the HONEST own-status mapping (<see cref="MapOwnStatus"/>) instead of the
    /// customer-facing <see cref="MapStatus"/> fold. Everything else matches
    /// <see cref="ToPendingOffer"/>.
    /// </summary>
    private static PendingOffer ToOwnPendingOffer(OfferWire wire) => new()
    {
        Id = wire.Id,
        RequestId = wire.RequestId,
        JeeberId = wire.JeeberId,
        Status = MapOwnStatus(wire.Status),
        CreatedAt = wire.CreatedAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = wire.UpdatedAt,
        Fee = ToDollars(wire.FeeCents),
        EtaMinutes = wire.EtaMinutes,
        Note = wire.Note,
    };

    /// <summary>
    /// The jeeber's-own-view status mapping (fix/offer-visibility). Unlike the
    /// customer-facing <see cref="MapStatus"/> (which folds every non-accept terminal
    /// state to <c>withdrawn</c>), the bidder's own list keeps the terminal reason
    /// honest: <c>rejected</c> / <c>superseded</c> / <c>expired</c> mean "the auction
    /// closed around you" → <see cref="PendingOfferStatus.Superseded"/> (the state the
    /// mobile app already renders as "not selected"), while <c>withdrawn</c> stays the
    /// jeeber's own retraction. Unknown upstream vocabulary falls back to
    /// <c>withdrawn</c>, matching <see cref="MapStatus"/>.
    /// </summary>
    private static string MapOwnStatus(string upstream) => upstream switch
    {
        "submitted" or "edited" or "pending" => PendingOfferStatus.Pending,
        "accepted" => PendingOfferStatus.Accepted,
        "rejected" or "superseded" or "expired" => PendingOfferStatus.Superseded,
        "withdrawn" => PendingOfferStatus.Withdrawn,
        _ => PendingOfferStatus.Withdrawn
    };

    // Fidelity gap: current offer-service submit emits generic code=conflict for
    // request_not_open and already_submitted. These typed-code matchers are
    // forward-compatible only; real code=conflict maps to OfferSubmitConflictException
    // until the separate offer-service-contract fix emits typed error codes.
    private static bool IsDuplicateCode(string? code)
        => code is not null
           && (code.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || code.Contains("already", StringComparison.OrdinalIgnoreCase)
               || code.Contains("offer_exists", StringComparison.OrdinalIgnoreCase));

    // Forward-compatible with the future typed-code contract: match request_not_open
    // and the looser not_open form case-insensitively so a closed auction can map
    // to its own 409 once offer-service stops emitting generic conflict here.
    private static bool IsRequestNotOpenCode(string? code)
        => code is not null
           && (code.Contains("request_not_open", StringComparison.OrdinalIgnoreCase)
               || code.Contains("not_open", StringComparison.OrdinalIgnoreCase));
}
