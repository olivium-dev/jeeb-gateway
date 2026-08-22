using JeebGateway.Requests;

namespace JeebGateway.Ratings.Jeeb;

/// <summary>
/// Shared delivery-completion invariant for every upstream-backed Jeeb rating
/// submission route. Rating remains a post-delivery action: legacy terminal
/// aliases are dual-read through <see cref="DeliveryStatusAlias"/>, while new
/// writes use the canonical <see cref="CanonicalDeliveryStatus.Done"/> token.
/// </summary>
public static class JeebRatingEligibility
{
    public static bool IsCompleted(string? deliveryStatus)
        => string.Equals(
            DeliveryStatusAlias.ToCanonical(deliveryStatus),
            CanonicalDeliveryStatus.Done,
            StringComparison.Ordinal);
}
