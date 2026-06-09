namespace JeebGateway.Availability;

/// <summary>
/// POST /requests/{requestId}/offers payload (T-backend-010 / JEEB-28).
/// The Jeeber quotes <see cref="Fee"/> (in the Client's currency, gross)
/// and <see cref="EtaMinutes"/> (pickup-to-dropoff estimate), with an
/// optional <see cref="Note"/>. The gateway re-validates every field
/// — never trust mobile-side validation alone.
/// </summary>
public class CreateOfferBody
{
    public decimal? Fee { get; set; }
    public int? EtaMinutes { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// S08 A3 — PUT /v1/offers/{offerId} payload. A jeeber edits their own pending
/// bid; every field is optional so a partial edit (the A3 body sends <c>fee</c>
/// only) leaves the unsent fields untouched. Same wire field names as
/// <see cref="CreateOfferBody"/> (<c>fee</c> in the client's currency, gross;
/// <c>etaMinutes</c>; <c>note</c>). offer-service owns the edit rule and the
/// ≤ 2-edits cap — the gateway only forwards the supplied fields.
/// </summary>
public class EditOfferBody
{
    public decimal? Fee { get; set; }
    public int? EtaMinutes { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Public projection of <see cref="PendingOffer"/>. Mirrors the offers
/// table in db/migrations/0007 minus the Client-side terminal timestamps
/// the gateway does not own yet.
/// </summary>
public class OfferDto
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public required string Status { get; init; }
    public required decimal Fee { get; init; }
    public required int EtaMinutes { get; init; }
    public string? Note { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
