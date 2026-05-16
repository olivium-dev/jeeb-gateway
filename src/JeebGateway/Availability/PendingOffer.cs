namespace JeebGateway.Availability;

/// <summary>
/// Lifecycle states for an offer extended to a Jeeber. The offer moves
/// from <see cref="Pending"/> exactly once — either to <see cref="Accepted"/>
/// (the Jeeber chose this request) or <see cref="Withdrawn"/> (auto-offline
/// sweeper, or a sibling offer was accepted).
/// </summary>
public static class PendingOfferStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// MVP shape of a pending offer record. The downstream offer-service owns
/// the canonical model (with quoted price, ETA, etc.) — the gateway only
/// holds the bits needed to authorize an accept and bind the request to
/// the Jeeber.
/// </summary>
public class PendingOffer
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
