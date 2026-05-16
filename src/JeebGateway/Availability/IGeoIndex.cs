namespace JeebGateway.Availability;

/// <summary>
/// Abstraction over the Redis geo index documented in
/// db/JEEBER_LOCATION_DESIGN.md. Keeps the global
/// <c>jeeber:online:geo</c> set and the per-vehicle
/// <c>jeeber:online:geo:&lt;vehicle_type&gt;</c> sets in sync.
/// </summary>
public interface IGeoIndex
{
    Task AddAsync(
        string userId,
        VehicleType vehicleType,
        double? longitude,
        double? latitude,
        CancellationToken ct);

    Task RemoveAsync(string userId, CancellationToken ct);

    Task<bool> ContainsAsync(string userId, CancellationToken ct);

    /// <summary>
    /// For inspection/testing. Returns the vehicle index a member is in,
    /// or null if it is not present.
    /// </summary>
    Task<VehicleType?> GetVehicleAsync(string userId, CancellationToken ct);
}
