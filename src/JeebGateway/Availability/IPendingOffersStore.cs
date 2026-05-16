namespace JeebGateway.Availability;

/// <summary>
/// Abstraction for the offer-service's offer ledger. Callers:
///
/// 1. The auto-offline sweeper (T-backend-023). When a Jeeber goes offline
///    we withdraw any in-flight offers so matching does not keep waiting
///    for a response from someone unreachable.
/// 2. The offer-accept endpoint (T-backend-039, BR-10). The Jeeber
///    accepts a specific offer they were extended; the gateway needs to
///    resolve <c>offerId → (jeeberId, requestId)</c> to authorize the
///    caller and bind the request to the Jeeber.
/// 3. The offer-submission endpoints (T-backend-010, FR-6.*). The Jeeber
///    submits a bid (<see cref="TrySubmitAsync"/>) and may retract it
///    before acceptance (<see cref="TryWithdrawAsync"/>). The gateway
///    enforces "max 20 offers per request" and "one live offer per Jeeber
///    per request" inside the store so the check and the write cannot race.
/// </summary>
public interface IPendingOffersStore
{
    /// <summary>
    /// Withdraws every <see cref="PendingOfferStatus.Pending"/> offer for
    /// the given Jeeber (auto-offline path). Already-accepted offers are
    /// not touched. Returns the number of offers transitioned.
    /// </summary>
    Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// Single-offer lookup used by the accept endpoint. Returns null when
    /// the offer is unknown.
    /// </summary>
    Task<PendingOffer?> GetAsync(string offerId, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="offerId"/> accepted and withdraws every other
    /// pending offer the Jeeber held — once a Jeeber commits to a request,
    /// their other in-flight offers become stale. Returns false when the
    /// offer is unknown or no longer in <see cref="PendingOfferStatus.Pending"/>.
    /// </summary>
    Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Atomic submit (T-backend-010). Under the store's write lock the
    /// store checks:
    /// <list type="bullet">
    ///   <item>The request does not already hold <paramref name="maxPerRequest"/>
    ///     live (<see cref="PendingOfferStatus.Pending"/>) offers — throws
    ///     <see cref="TooManyOffersForRequestException"/> if so.</item>
    ///   <item>This <paramref name="jeeberId"/> does not already have a live
    ///     offer on this request — throws <see cref="DuplicateOfferException"/>
    ///     if so. A previously <see cref="PendingOfferStatus.Withdrawn"/> offer
    ///     does NOT block re-submission (acceptance criterion: withdraw+re-offer).</item>
    /// </list>
    /// Returns the new offer in the <see cref="PendingOfferStatus.Pending"/> state.
    /// </summary>
    Task<PendingOffer> TrySubmitAsync(
        string requestId,
        string jeeberId,
        decimal fee,
        int etaMinutes,
        string? note,
        int maxPerRequest,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Atomic withdraw (T-backend-010). Only the owning Jeeber may retract
    /// the offer, and only while it is still <see cref="PendingOfferStatus.Pending"/>.
    /// Returns <see cref="WithdrawOfferOutcome.Withdrawn"/> on success.
    /// Returns <see cref="WithdrawOfferOutcome.NotFound"/> when the id is
    /// unknown (or the offer belongs to a different request than the one
    /// in the URL — keeps the surface 404-clean for callers).
    /// Returns <see cref="WithdrawOfferOutcome.NotOwned"/> when a different
    /// Jeeber tries to withdraw the offer (controller maps to 403).
    /// Returns <see cref="WithdrawOfferOutcome.NotPending"/> when the offer
    /// has already moved to accepted / withdrawn (controller maps to 409).
    /// </summary>
    Task<WithdrawOfferOutcome> TryWithdrawAsync(
        string offerId,
        string requestId,
        string jeeberId,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Returns every offer (any status) attached to <paramref name="requestId"/>,
    /// newest-first. Used by integration tests and the future Client
    /// "see all bids" listing endpoint.
    /// </summary>
    Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(
        string requestId, CancellationToken ct);
}

/// <summary>
/// Outcomes for <see cref="IPendingOffersStore.TryWithdrawAsync"/>. The
/// controller maps each to a distinct HTTP status / ProblemDetails type
/// so the mobile app can render the right banner.
/// </summary>
public enum WithdrawOfferOutcome
{
    Withdrawn,
    NotFound,
    NotOwned,
    NotPending
}

/// <summary>
/// T-backend-010 acceptance criterion: a request may hold at most 20 live
/// offers. The controller maps this to 409 ProblemDetails.
/// </summary>
public class TooManyOffersForRequestException : Exception
{
    public string RequestId { get; }
    public int LiveCount { get; }
    public int Limit { get; }

    public TooManyOffersForRequestException(string requestId, int liveCount, int limit)
        : base($"Request '{requestId}' already has {liveCount} live offers (limit {limit}).")
    {
        RequestId = requestId;
        LiveCount = liveCount;
        Limit = limit;
    }
}

/// <summary>
/// T-backend-010 acceptance criterion: at most one live offer per Jeeber
/// per request. Mirrors the partial unique index
/// <c>offers_request_jeeber_uniq</c> in db/migrations/0007 (the DB version
/// applies to all rows; the gateway tolerates re-submission once the prior
/// offer was withdrawn so the mobile app can correct a fat-finger bid).
/// </summary>
public class DuplicateOfferException : Exception
{
    public string RequestId { get; }
    public string JeeberId { get; }
    public string ExistingOfferId { get; }

    public DuplicateOfferException(string requestId, string jeeberId, string existingOfferId)
        : base($"Jeeber '{jeeberId}' already has a live offer on request '{requestId}' (offer '{existingOfferId}').")
    {
        RequestId = requestId;
        JeeberId = jeeberId;
        ExistingOfferId = existingOfferId;
    }
}
