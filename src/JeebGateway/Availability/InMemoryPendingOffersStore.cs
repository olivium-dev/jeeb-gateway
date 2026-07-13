using System.Collections.Concurrent;

namespace JeebGateway.Availability;

/// <summary>
/// MVP in-memory offer ledger. Tests seed offers via
/// <see cref="EnqueueForTest"/> (legacy accept-flow tests) or by hitting
/// <see cref="TrySubmitAsync"/> from the submission tests; production
/// swaps for an offer-service client behind the same
/// <see cref="IPendingOffersStore"/> contract.
///
/// Concurrency: lookups go through the underlying ConcurrentDictionary;
/// state-changing operations (submit, withdraw, accept) take the write
/// lock so the multi-row transitions ("accept this, withdraw the others"
/// and "count live offers, then insert") are atomic.
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
    /// Test seam — registers an offer in <see cref="PendingOfferStatus.Pending"/>
    /// with synthetic fee/eta values. Production wiring will receive offers
    /// from the offer-service event stream and call into the same dictionary
    /// via an internal path.
    /// </summary>
    public PendingOffer EnqueueForTest(string jeeberId, string requestId, string? offerId = null)
    {
        var offer = new PendingOffer
        {
            Id = offerId ?? Guid.NewGuid().ToString(),
            RequestId = requestId,
            JeeberId = jeeberId,
            Status = PendingOfferStatus.Pending,
            CreatedAt = _clock.GetUtcNow(),
            Fee = 1m,
            EtaMinutes = 30,
            Note = null
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
            // offers are now stale if a sibling accept came in milliseconds
            // later on a different connection. Withdraw them in the same
            // critical section.
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

    public Task<AcceptOfferOutcome> AcceptWithSupersedeAsync(
        string offerId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_offers.TryGetValue(offerId, out var offer))
            {
                return Task.FromResult(AcceptOfferOutcome.NotFound);
            }

            // Re-accept / already-closed auction. SM-2: re-accepting (or accepting
            // a competing bid after a winner exists) returns 409 already_accepted
            // and surfaces the WINNER. Resolve the winner from the request's
            // accepted offer so the controller can return its jeeberId regardless
            // of whether THIS offer is the winner or a superseded loser.
            if (offer.Status != PendingOfferStatus.Pending)
            {
                var winner = FindAcceptedForRequest(offer.RequestId);
                if (winner is not null)
                {
                    return Task.FromResult(AcceptOfferOutcome.AlreadyAccepted(winner.JeeberId));
                }

                // No accepted winner on the request, yet this offer is non-pending
                // (the Jeeber withdrew it). That is a plain "no longer acceptable",
                // not an already_accepted close.
                return Task.FromResult(AcceptOfferOutcome.NotPending);
            }

            // Defensive: another offer on this request may already be accepted even
            // though THIS offer is still pending (a superseded-sweep gap on a prior
            // partial accept). Treat the request as closed and do not double-accept.
            var existingWinner = FindAcceptedForRequest(offer.RequestId);
            if (existingWinner is not null && !ReferenceEquals(existingWinner, offer))
            {
                offer.Status = PendingOfferStatus.Superseded;
                offer.UpdatedAt = at;
                return Task.FromResult(AcceptOfferOutcome.AlreadyAccepted(existingWinner.JeeberId));
            }

            offer.Status = PendingOfferStatus.Accepted;
            offer.UpdatedAt = at;

            // ACC-02 — the auction closes around this single winner. Every OTHER
            // pending offer on the SAME request (any Jeeber) is superseded in the
            // same critical section, so a concurrent accept of a competing bid on
            // another connection cannot also win.
            var supersededCount = 0;
            foreach (var sibling in _offers.Values)
            {
                if (ReferenceEquals(sibling, offer)) continue;
                if (!string.Equals(sibling.RequestId, offer.RequestId, StringComparison.Ordinal)) continue;
                if (sibling.Status != PendingOfferStatus.Pending) continue;
                sibling.Status = PendingOfferStatus.Superseded;
                sibling.UpdatedAt = at;
                supersededCount++;
            }

            return Task.FromResult(AcceptOfferOutcome.Accepted(offer.JeeberId, supersededCount));
        }
    }

    /// <summary>
    /// Returns the accepted offer on <paramref name="requestId"/> (the auction
    /// winner) or null when the auction is still open. Caller holds the write lock.
    /// </summary>
    private PendingOffer? FindAcceptedForRequest(string requestId)
    {
        foreach (var candidate in _offers.Values)
        {
            if (!string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal)) continue;
            if (candidate.Status == PendingOfferStatus.Accepted) return candidate;
        }
        return null;
    }

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
    {
        lock (_writeLock)
        {
            if (!_offers.TryGetValue(offerId, out var offer))
            {
                return Task.FromResult(EditOfferOutcome.NotFound);
            }
            // Id collides with an offer on a different request — surface as 404,
            // mirroring TryWithdrawAsync's request-scoping.
            if (!string.Equals(offer.RequestId, requestId, StringComparison.Ordinal))
            {
                return Task.FromResult(EditOfferOutcome.NotFound);
            }
            if (!string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal))
            {
                return Task.FromResult(EditOfferOutcome.NotOwned);
            }
            if (offer.Status != PendingOfferStatus.Pending)
            {
                return Task.FromResult(EditOfferOutcome.NotPending);
            }

            // SM-2 / JEB-1474 2-edit cap. The check + the increment happen under the
            // same lock so two concurrent edits cannot both pass the cap. The cap is
            // evaluated BEFORE mutating, so a rejected 3rd edit leaves the bid intact.
            if (offer.EditCount >= maxEdits)
            {
                return Task.FromResult(EditOfferOutcome.EditLimitReached);
            }

            if (fee is decimal f) offer.Fee = f;
            if (etaMinutes is int e) offer.EtaMinutes = e;
            if (note is not null) offer.Note = note;
            offer.EditCount++;
            offer.UpdatedAt = at;

            return Task.FromResult(EditOfferOutcome.Edited(offer));
        }
    }

    public Task<PendingOffer> TrySubmitAsync(
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
        // clientId is an upstream-mirror hint only; the in-memory store owns its
        // own request graph and never mirrors, so it is intentionally ignored.
        _ = clientId;

        lock (_writeLock)
        {
            // Scan once and capture both pieces of state we need: the live
            // offer count for the request (cap check) and any existing live
            // offer from this Jeeber (one-per-Jeeber check). Doing it inside
            // the write lock means a concurrent submit cannot squeeze in
            // between the read and the insert.
            var liveCount = 0;
            PendingOffer? existingForJeeber = null;
            foreach (var offer in _offers.Values)
            {
                if (!string.Equals(offer.RequestId, requestId, StringComparison.Ordinal)) continue;
                if (offer.Status != PendingOfferStatus.Pending) continue;
                liveCount++;
                if (string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal))
                {
                    existingForJeeber = offer;
                }
            }

            if (existingForJeeber is not null)
            {
                throw new DuplicateOfferException(requestId, jeeberId, existingForJeeber.Id);
            }

            if (liveCount >= maxPerRequest)
            {
                throw new TooManyOffersForRequestException(requestId, liveCount, maxPerRequest);
            }

            var created = new PendingOffer
            {
                Id = Guid.NewGuid().ToString(),
                RequestId = requestId,
                JeeberId = jeeberId,
                Status = PendingOfferStatus.Pending,
                CreatedAt = at,
                Fee = fee,
                EtaMinutes = etaMinutes,
                Note = note
            };
            _offers[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<WithdrawOfferOutcome> TryWithdrawAsync(
        string offerId,
        string requestId,
        string jeeberId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_offers.TryGetValue(offerId, out var offer))
            {
                return Task.FromResult(WithdrawOfferOutcome.NotFound);
            }
            // 404 — id collides with an offer on a different request. We
            // surface this as NotFound rather than as a generic mismatch so
            // the controller stays free of guesses about the caller's intent.
            if (!string.Equals(offer.RequestId, requestId, StringComparison.Ordinal))
            {
                return Task.FromResult(WithdrawOfferOutcome.NotFound);
            }
            if (!string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal))
            {
                return Task.FromResult(WithdrawOfferOutcome.NotOwned);
            }
            if (offer.Status != PendingOfferStatus.Pending)
            {
                return Task.FromResult(WithdrawOfferOutcome.NotPending);
            }

            offer.Status = PendingOfferStatus.Withdrawn;
            offer.UpdatedAt = at;
            return Task.FromResult(WithdrawOfferOutcome.Withdrawn);
        }
    }

    public Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(
        string requestId, CancellationToken ct)
    {
        var snapshot = _offers.Values
            .Where(o => string.Equals(o.RequestId, requestId, StringComparison.Ordinal))
            .OrderByDescending(o => o.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<PendingOffer>>(snapshot);
    }

    /// <summary>
    /// F4 (JEBV4-301) — true batch: one pass over the offer snapshot tallies the count for
    /// every requested id, instead of the default interface fan-out's N separate scans.
    /// Every requested id is present in the result (0 when it has no offers), matching the
    /// interface contract.
    /// </summary>
    public Task<IReadOnlyDictionary<string, int>> CountForRequestsAsync(
        IReadOnlyCollection<string> requestIds, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in requestIds)
        {
            if (!string.IsNullOrEmpty(id)) counts[id] = 0; // seed so absent-offer ids read 0
        }

        foreach (var offer in _offers.Values)
        {
            if (counts.ContainsKey(offer.RequestId))
                counts[offer.RequestId]++;
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
    }

    public Task<IReadOnlyList<PendingOffer>> ListForJeeberAsync(
        string jeeberId, CancellationToken ct)
    {
        // fix/offer-visibility (run-23 CHECK C): ANY status — a jeeber's own list must
        // keep showing their terminal (accepted / superseded / withdrawn) offers, not
        // just the live ones. Newest-first to match ListForRequestAsync.
        var snapshot = _offers.Values
            .Where(o => string.Equals(o.JeeberId, jeeberId, StringComparison.Ordinal))
            .OrderByDescending(o => o.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<PendingOffer>>(snapshot);
    }

    public Task<int> ExpireForRequestAsync(string requestId, DateTimeOffset at, CancellationToken ct)
    {
        // Request expired with no winner: every still-pending bid on it is now stale.
        // Supersede them under the write lock so the transition is atomic w.r.t. a
        // concurrent submit/accept on the same request. Accepted / withdrawn / already
        // superseded offers are left untouched (terminal).
        lock (_writeLock)
        {
            var closed = 0;
            foreach (var offer in _offers.Values)
            {
                if (!string.Equals(offer.RequestId, requestId, StringComparison.Ordinal)) continue;
                if (offer.Status != PendingOfferStatus.Pending) continue;
                offer.Status = PendingOfferStatus.Superseded;
                offer.UpdatedAt = at;
                closed++;
            }
            return Task.FromResult(closed);
        }
    }
}
