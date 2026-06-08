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
    /// Resolves the requestId an offer was submitted against. Returns
    /// <c>null</c> when the offer is unknown to this gateway instance.
    /// </summary>
    string? ResolveRequestId(string offerId);
}

/// <summary>
/// In-process, thread-safe <see cref="IOfferRequestIndex"/>. Registered as a
/// singleton so the pairing learned by <c>RequestOffersController.Submit</c> is
/// visible to <c>OffersController.Accept</c> on the same gateway instance.
/// </summary>
public sealed class InMemoryOfferRequestIndex : IOfferRequestIndex
{
    private readonly ConcurrentDictionary<string, string> _byOfferId = new(StringComparer.Ordinal);

    public void Record(string offerId, string requestId)
    {
        if (string.IsNullOrWhiteSpace(offerId) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        _byOfferId[offerId] = requestId;
    }

    public string? ResolveRequestId(string offerId)
        => _byOfferId.TryGetValue(offerId, out var requestId) ? requestId : null;
}
