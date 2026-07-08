namespace JeebGateway.Tiers;

/// <summary>
/// Delivery tier configuration (T-backend-009). A tier groups the SLA window,
/// service radius, commission rate, and a human-readable price hint shown to
/// the Client when picking a tier for a new delivery request.
///
/// Three default tiers are seeded on startup (Urgent, Same-Day, Scheduled);
/// admins may edit canonical tier attributes, but the catalog remains fixed
/// to those three tier ids.
/// Tier changes are read fresh on every request so an in-flight request is
/// never partially affected by a concurrent update.
/// </summary>
public class DeliveryTier
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required int SlaHours { get; set; }
    public required double RadiusKm { get; set; }
    public required int RequestTtlSeconds { get; set; }
    public required double CommissionRate { get; set; }
    public required string PriceHint { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
