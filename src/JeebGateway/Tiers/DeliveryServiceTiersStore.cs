using JeebGateway.Infrastructure;
using JeebGateway.Services.Clients;

namespace JeebGateway.Tiers;

/// <summary>
/// Read-through tier catalog owned by delivery-service. Its current contract is
/// read-only; admin mutations fail closed until the owner publishes write routes.
/// </summary>
public sealed class DeliveryServiceTiersStore : ITiersStore
{
    private readonly IDeliveryServiceClient _owner;

    public DeliveryServiceTiersStore(IDeliveryServiceClient owner) => _owner = owner;

    public async Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct) =>
        (await _owner.ListTiersAsync(ct)).Select(Map).ToArray();

    public async Task<DeliveryTier?> GetAsync(string id, CancellationToken ct) =>
        (await _owner.ListTiersAsync(ct)).Where(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            .Select(Map).SingleOrDefault();

    public Task<DeliveryTier> CreateAsync(DeliveryTierCreate input, string adminUserId, CancellationToken ct) =>
        Unsupported<DeliveryTier>("delivery-service tier create");

    public Task<DeliveryTier?> ReplaceAsync(string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct) =>
        Unsupported<DeliveryTier?>("delivery-service tier replace");

    public Task<bool> DeleteAsync(string id, CancellationToken ct) =>
        Unsupported<bool>("delivery-service tier delete");

    private static DeliveryTier Map(DeliveryTierDto row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        SlaHours = row.SlaHours,
        RadiusKm = row.RadiusKm,
        RequestTtlSeconds = row.RequestTtlSeconds,
        CommissionRate = row.CommissionRate,
        PriceHint = row.PriceHint,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static Task<T> Unsupported<T>(string capability) =>
        Task.FromException<T>(new OwnerCapabilityUnavailableException(capability));
}
