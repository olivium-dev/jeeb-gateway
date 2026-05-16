namespace JeebGateway.Availability;

/// <summary>
/// Abstraction for the offer-service's pending-offer queue. Two callers:
///
/// 1. The auto-offline sweeper (T-backend-023). When a Jeeber goes offline
///    we withdraw any in-flight offers so matching does not keep waiting
///    for a response from someone unreachable.
/// 2. The offer-accept endpoint (T-backend-039, BR-10). The Jeeber
///    accepts a specific offer they were extended; the gateway needs to
///    resolve <c>offerId → (jeeberId, requestId)</c> to authorize the
///    caller and bind the request to the Jeeber.
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
}
