using System.Collections.Concurrent;

namespace JeebGateway.Availability;

/// <summary>
/// Thin-BFF routing index mapping <c>offerId → requestId</c>.
///
/// <para><b>Why this exists.</b> The mobile accept route is offer-scoped
/// (<c>POST /offers/{offerId}/accept</c>) while the canonical offer-service
/// accept route is request-scoped
/// (<c>POST /api/v1/requests/{requestId}/offers/{offerId}/accept</c>) and
/// offer-service exposes no get-offer-by-id route to recover the requestId from
/// the offer. Because every offer is created through the gateway's
/// <c>POST /requests/{requestId}/offers</c> path, the gateway already knows the
/// pairing at submit time and records it here so the accept path can forward to
/// the correct upstream route.</para>
///
/// <para><b>This is a routing concern, not auction domain state.</b> It holds
/// only the immutable structural pairing needed to dispatch a request — no fee,
/// status, ownership, lifecycle, or any field the offer-service owns. The
/// gateway remains a thin, stateless BFF: the offer-service is still the sole
/// owner of every auction rule and every mutable offer fact (see the
/// microservices-no-inter-service-coupling architecture law — composition lives
/// in the BFF, domain state does not). A gateway restart simply re-learns
/// pairings as offers are submitted; an unknown offer at accept time resolves to
/// a 404 (phantom offer), which is the correct contract.</para>
/// </summary>
public interface IOfferRequestIndex
{
    /// <summary>
    /// Records (or overwrites) the <c>offerId → requestId</c> pairing learned at
    /// offer-submission time. Idempotent: re-recording the same pair is a no-op.
    /// </summary>
    void Record(string offerId, string requestId);

    /// <summary>
    /// Records the <c>offerId → (requestId, jeeberId)</c> pairing learned at
    /// offer-submission time. The <paramref name="jeeberId"/> is the immutable
    /// bidder identity captured when the offer was submitted through this gateway;
    /// it lets the accept path detect a genuine BR-1 self-offer (the accepting
    /// CLIENT is also the jeeber who bid the offer) WITHOUT an extra offer-service
    /// round-trip and WITHOUT recomputing any auction rule. This is a structural
    /// routing fact, not mutable auction state. Idempotent.
    /// </summary>
    void Record(string offerId, string requestId, string? jeeberId);

    /// <summary>
    /// Resolves the requestId an offer was submitted against. Returns
    /// <c>null</c> when the offer is unknown to this gateway instance.
    /// </summary>
    string? ResolveRequestId(string offerId);

    /// <summary>
    /// Resolves the jeeber (bidder) identity recorded for an offer at submit time.
    /// Returns <c>null</c> when the offer is unknown to this gateway instance, or
    /// when the pairing was recorded without a jeeber id (legacy
    /// <see cref="Record(string,string)"/> overload). Callers must treat a
    /// <c>null</c> result as "unknown — cannot assert a self-offer" and defer the
    /// BR-1 self-offer decision to the offer-service, never as "not a self-offer".
    /// </summary>
    string? ResolveJeeberId(string offerId);
}

/// <summary>
/// In-process, thread-safe <see cref="IOfferRequestIndex"/>. Registered as a
/// singleton so the pairing learned by <c>RequestOffersController.Submit</c> is
/// visible to <c>OffersController.Accept</c> on the same gateway instance.
/// </summary>
public sealed class InMemoryOfferRequestIndex : IOfferRequestIndex
{
    private readonly record struct Pairing(string RequestId, string? JeeberId);

    private readonly ConcurrentDictionary<string, Pairing> _byOfferId = new(StringComparer.Ordinal);

    public void Record(string offerId, string requestId)
        => Record(offerId, requestId, jeeberId: null);

    public void Record(string offerId, string requestId, string? jeeberId)
    {
        if (string.IsNullOrWhiteSpace(offerId) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var normalizedJeeberId = string.IsNullOrWhiteSpace(jeeberId) ? null : jeeberId;
        _byOfferId[offerId] = new Pairing(requestId, normalizedJeeberId);
    }

    public string? ResolveRequestId(string offerId)
        => _byOfferId.TryGetValue(offerId, out var pairing) ? pairing.RequestId : null;

    public string? ResolveJeeberId(string offerId)
        => _byOfferId.TryGetValue(offerId, out var pairing) ? pairing.JeeberId : null;
}
