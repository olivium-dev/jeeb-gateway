namespace JeebGateway.Availability;

/// <summary>
/// Lifecycle states for an offer extended to a Jeeber. The offer moves
/// from <see cref="Pending"/> exactly once — either to <see cref="Accepted"/>
/// (the Jeeber chose this request) or <see cref="Withdrawn"/> (auto-offline
/// sweeper, a sibling offer was accepted, or the Jeeber explicitly retracted
/// the bid via DELETE /requests/{id}/offers/{offerId}).
/// </summary>
public static class PendingOfferStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// MVP shape of an offer record. The downstream offer-service owns the
/// canonical model — the gateway keeps the bid fields (<see cref="Fee"/>,
/// <see cref="EtaMinutes"/>, <see cref="Note"/>) so the reverse-auction
/// flow (T-backend-010, FR-6.*) can run end-to-end while the offer-service
/// is being built out.
/// </summary>
public class PendingOffer
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Quoted fee the Jeeber asks the Client to pay (gross). Must be
    /// strictly positive; the controller enforces the MVP floor of
    /// $1 (BR / T-backend-010 acceptance criteria) and the DB CHECK
    /// in 0007_init_offers enforces <c>fee &gt; 0</c>.
    /// </summary>
    public decimal Fee { get; init; }

    /// <summary>
    /// Quoted pickup-to-dropoff estimate in minutes (FR-6.2). Must be
    /// strictly positive — mirrors the DB CHECK <c>eta_minutes &gt; 0</c>.
    /// </summary>
    public int EtaMinutes { get; init; }

    /// <summary>
    /// Optional free-text message from the Jeeber to the Client. Capped at
    /// 500 chars to match the DB CHECK in 0007.
    /// </summary>
    public string? Note { get; init; }
}
