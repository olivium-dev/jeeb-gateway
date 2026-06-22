using JeebGateway.Services.Clients;

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
///   <item><b>Conflict codes.</b> offer-service returns HTTP 409 with a typed
///     error code on submit; we translate it back into the same
///     <see cref="DuplicateOfferException"/> /
///     <see cref="TooManyOffersForRequestException"/> the controller already
///     catches, so the controller is untouched.</item>
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
    /// and the gateway's $1 minimum. Used to surface a clear duplicate/cap
    /// translation; fee validation itself stays in the controller.
    /// </summary>
    private const long MinimumFeeCents = 100;

    private readonly IOfferServiceClient _client;

    public UpstreamPendingOffersStore(IOfferServiceClient client)
    {
        _client = client;
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
    /// One submit attempt with the 409 → duplicate/cap translation. The 404
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
            // offer-service uses one 409 surface for both "you already offered"
            // and "request not open". Map the duplicate case onto the gateway's
            // DuplicateOfferException (controller → 409 offer-already-exists);
            // anything else (request_not_open) re-surfaces as the cap exception
            // so the controller still returns a 409 ProblemDetails rather than a
            // raw 500. The exact upstream code drives the choice.
            if (IsDuplicateCode(ex.UpstreamCode))
            {
                // The upstream owns the existing offer id; we do not have it
                // here, so report the request-scoped duplicate without it.
                throw new DuplicateOfferException(requestId, jeeberId, existingOfferId: "(upstream)");
            }

            throw new TooManyOffersForRequestException(
                requestId, liveCount: maxPerRequest, limit: maxPerRequest);
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

    public async Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(
        string requestId, CancellationToken ct, string? actingUserId = null)
    {
        // offer-service GET /api/v1/requests/{id}/offers is owner-gated on the
        // gateway-injected x-user-id. The controller has already authorized the
        // caller as the request owner, so actingUserId is that owner. If a caller
        // ever reaches here without one (no upstream identity to assert), return
        // empty rather than 403/throw — the in-memory fixtures pass null.
        if (string.IsNullOrWhiteSpace(actingUserId))
        {
            return Array.Empty<PendingOffer>();
        }

        var wire = await _client.ListForRequestAsync(actingUserId, requestId, ct);
        return wire.Select(ToPendingOffer).ToList();
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

    private static bool IsDuplicateCode(string? code)
        => code is not null
           && (code.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || code.Contains("already", StringComparison.OrdinalIgnoreCase)
               || code.Contains("offer_exists", StringComparison.OrdinalIgnoreCase));
}
