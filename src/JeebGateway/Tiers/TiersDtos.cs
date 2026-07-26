namespace JeebGateway.Tiers;

public class DeliveryTierDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int SlaHours { get; init; }
    public required double RadiusKm { get; init; }
    public int RequestTtlSeconds { get; init; }
    public required double CommissionRate { get; init; }
    public required string PriceHint { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public class DeliveryTiersListResponse
{
    public required IReadOnlyList<DeliveryTierDto> Items { get; init; }
}

public class DeliveryTierCreateRequest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int? SlaHours { get; set; }
    public double? RadiusKm { get; set; }
    public int? RequestTtlSeconds { get; set; }
    public double? CommissionRate { get; set; }
    public string? PriceHint { get; set; }
}

public class DeliveryTierReplaceRequest
{
    /// <summary>
    /// P7 (G-I): explicit acknowledgement that changing <see cref="RequestTtlSeconds"/>
    /// MOVES the derived offer-wait deadline of every IN-FLIGHT (pending/matched)
    /// request on this tier — the deadline is derived, not stored, so a TTL edit is
    /// retroactive by construction. Absent/false + an actual TTL change ⇒ 409
    /// (with an <c>affectedCount</c> ProblemDetails extension); true ⇒ apply anyway.
    /// Additive and defaulted false, so an existing caller that never changes the TTL
    /// is unaffected.
    /// </summary>
    public bool ApplyToInFlight { get; set; }

    public string? Name { get; set; }
    public int? SlaHours { get; set; }
    public double? RadiusKm { get; set; }
    public int? RequestTtlSeconds { get; set; }
    public double? CommissionRate { get; set; }
    public string? PriceHint { get; set; }
}
