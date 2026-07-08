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
    public string? Name { get; set; }
    public int? SlaHours { get; set; }
    public double? RadiusKm { get; set; }
    public int? RequestTtlSeconds { get; set; }
    public double? CommissionRate { get; set; }
    public string? PriceHint { get; set; }
}
