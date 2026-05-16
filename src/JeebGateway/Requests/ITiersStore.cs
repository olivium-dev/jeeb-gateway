namespace JeebGateway.Requests;

/// <summary>
/// Catalog of delivery tiers (FR-4.1). The MVP ships a static five-tier
/// table seeded by db/migrations/0011 — the store abstraction exists so
/// the production wiring can hit Postgres (or an NSwag-generated client
/// to delivery-service) without rewriting the controller.
/// </summary>
public interface ITiersStore
{
    /// <summary>
    /// Returns true when a tier with the supplied code exists and is
    /// active. Used at request-creation time to enforce T-backend-007's
    /// "validate tier exists" acceptance criterion. The lookup is
    /// case-sensitive — tier codes are stable lowercase identifiers
    /// per the DB CHECK constraint <c>delivery_tiers_code_format</c>.
    /// </summary>
    Task<bool> ExistsAsync(string tierCode, CancellationToken ct);
}

/// <summary>
/// Static catalog matching the 0011 seed. The set is not runtime-mutable
/// on purpose — tier changes flow through the admin moderation path, not
/// in-process state. The production swap moves the seed list behind an
/// IOptions binding or a downstream client.
/// </summary>
public class InMemoryTiersStore : ITiersStore
{
    private static readonly HashSet<string> ActiveCodes = new(StringComparer.Ordinal)
    {
        "flash",
        "express",
        "standard",
        "on_the_way",
        "eco"
    };

    public Task<bool> ExistsAsync(string tierCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierCode)) return Task.FromResult(false);
        return Task.FromResult(ActiveCodes.Contains(tierCode));
    }
}
