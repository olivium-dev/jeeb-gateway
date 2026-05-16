using System.Collections.Concurrent;

namespace JeebGateway.Availability;

/// <summary>
/// MVP in-memory pending-offers ledger. Tests seed offers via
/// <see cref="EnqueueForTest"/>; production swaps for an offer-service
/// client behind the same <see cref="IPendingOffersStore"/> contract.
///
/// Concurrency: lookups go through the underlying ConcurrentDictionary;
/// state-changing operations (withdraw, accept) take the write lock so
/// the multi-row "accept this, withdraw the others" transition is atomic.
/// </summary>
public class InMemoryPendingOffersStore : IPendingOffersStore
{
    private readonly ConcurrentDictionary<string, PendingOffer> _offers = new();
    private readonly object _writeLock = new();
    private readonly TimeProvider _clock;

    public InMemoryPendingOffersStore(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Test seam — registers an offer in <see cref="PendingOfferStatus.Pending"/>.
    /// Production wiring will receive offers from the offer-service event
    /// stream and call into the same dictionary via an internal path.
    /// </summary>
    public PendingOffer EnqueueForTest(string jeeberId, string requestId, string? offerId = null)
    {
        var offer = new PendingOffer
        {
            Id = offerId ?? Guid.NewGuid().ToString(),
            RequestId = requestId,
            JeeberId = jeeberId,
            Status = PendingOfferStatus.Pending,
            CreatedAt = _clock.GetUtcNow()
        };
        _offers[offer.Id] = offer;
        return offer;
    }

    /// <summary>
    /// Returns the offers that are still in flight for <paramref name="jeeberId"/>
    /// (status = <see cref="PendingOfferStatus.Pending"/>). Withdrawn /
    /// accepted offers are filtered out so test assertions can use
    /// "no offers in flight" as an empty collection.
    /// </summary>
    public IReadOnlyCollection<PendingOffer> PeekForTest(string jeeberId)
        => _offers.Values
            .Where(o => string.Equals(o.JeeberId, jeeberId, StringComparison.Ordinal)
                        && o.Status == PendingOfferStatus.Pending)
            .ToArray();

    public Task<PendingOffer?> GetAsync(string offerId, CancellationToken ct)
    {
        _offers.TryGetValue(offerId, out var offer);
        return Task.FromResult<PendingOffer?>(offer);
    }

    public Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var withdrawn = 0;
            var now = _clock.GetUtcNow();
            foreach (var offer in _offers.Values)
            {
                if (!string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal)) continue;
                if (offer.Status != PendingOfferStatus.Pending) continue;
                offer.Status = PendingOfferStatus.Withdrawn;
                offer.UpdatedAt = now;
                withdrawn++;
            }
            return Task.FromResult(withdrawn);
        }
    }

    public Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_offers.TryGetValue(offerId, out var offer)) return Task.FromResult(false);
            if (offer.Status != PendingOfferStatus.Pending) return Task.FromResult(false);

            offer.Status = PendingOfferStatus.Accepted;
            offer.UpdatedAt = at;

            // The Jeeber committed to one request; their other in-flight
            // offers are now stale and must not race with the BR-10 cap if
            // a sibling accept came in milliseconds later on a different
            // connection. Withdraw them in the same critical section.
            foreach (var sibling in _offers.Values)
            {
                if (ReferenceEquals(sibling, offer)) continue;
                if (!string.Equals(sibling.JeeberId, offer.JeeberId, StringComparison.Ordinal)) continue;
                if (sibling.Status != PendingOfferStatus.Pending) continue;
                sibling.Status = PendingOfferStatus.Withdrawn;
                sibling.UpdatedAt = at;
            }

            return Task.FromResult(true);
        }
    }
}
