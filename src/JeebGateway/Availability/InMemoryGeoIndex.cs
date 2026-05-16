using System.Collections.Concurrent;

namespace JeebGateway.Availability;

/// <summary>
/// In-memory stand-in for the Redis geo index. Models the global +
/// per-vehicle membership semantics so the controller and sweeper can
/// be exercised in tests; production wiring swaps this for a Redis
/// implementation behind the same <see cref="IGeoIndex"/> contract.
/// </summary>
public class InMemoryGeoIndex : IGeoIndex
{
    private sealed record GeoEntry(VehicleType Vehicle, double? Longitude, double? Latitude);

    private readonly ConcurrentDictionary<string, GeoEntry> _entries = new();

    public Task AddAsync(string userId, VehicleType vehicleType, double? longitude, double? latitude, CancellationToken ct)
    {
        _entries[userId] = new GeoEntry(vehicleType, longitude, latitude);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string userId, CancellationToken ct)
    {
        _entries.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync(string userId, CancellationToken ct)
        => Task.FromResult(_entries.ContainsKey(userId));

    public Task<VehicleType?> GetVehicleAsync(string userId, CancellationToken ct)
        => Task.FromResult(_entries.TryGetValue(userId, out var entry) ? entry.Vehicle : (VehicleType?)null);
}
