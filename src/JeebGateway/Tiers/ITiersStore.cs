namespace JeebGateway.Tiers;

/// <summary>
/// Storage abstraction for the delivery tier catalog. The default in-memory
/// implementation is intended for early-MVP local runs and integration tests;
/// production wiring will hit Postgres via a follow-up migration colocated
/// with the delivery_requests schema.
///
/// All reads return cloned snapshots so concurrent mutations cannot leak
/// partial state into an in-flight request — satisfying the "tier changes
/// take effect on next request only" acceptance criterion.
/// </summary>
public interface ITiersStore
{
    Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct);

    Task<DeliveryTier?> GetAsync(string id, CancellationToken ct);

    Task<DeliveryTier> CreateAsync(DeliveryTierCreate input, string adminUserId, CancellationToken ct);

    Task<DeliveryTier?> ReplaceAsync(string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);
}

public class DeliveryTierCreate
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required int SlaHours { get; init; }
    public required double RadiusKm { get; init; }
    public required int RequestTtlSeconds { get; init; }
    public required double CommissionRate { get; init; }
    public required string PriceHint { get; init; }
}

public class DeliveryTierReplace
{
    public required string Name { get; init; }
    public required int SlaHours { get; init; }
    public required double RadiusKm { get; init; }
    public required int RequestTtlSeconds { get; init; }
    public required double CommissionRate { get; init; }
    public required string PriceHint { get; init; }
}

public class DuplicateTierIdException : Exception
{
    public DuplicateTierIdException(string id)
        : base($"A tier with id '{id}' already exists.") { }
}

public class DuplicateTierNameException : Exception
{
    public DuplicateTierNameException(string name)
        : base($"A tier named '{name}' already exists.") { }
}
