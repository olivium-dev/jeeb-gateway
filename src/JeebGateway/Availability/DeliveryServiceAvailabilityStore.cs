using JeebGateway.Infrastructure;
using JeebGateway.Services.Clients;

namespace JeebGateway.Availability;

/// <summary>Stateless availability projection over delivery-service.</summary>
public sealed class DeliveryServiceAvailabilityStore : IAvailabilityStore
{
    private readonly IDeliveryServiceClient _owner;

    public DeliveryServiceAvailabilityStore(IDeliveryServiceClient owner) => _owner = owner;

    public async Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
    {
        var row = await _owner.GetAvailabilityAsync(userId, ct);
        return row is null
            ? new JeeberAvailability
            {
                UserId = userId,
                IsOnline = false,
                VehicleType = VehicleType.Car,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            }
            : Map(row);
    }

    public async Task<GoOnlineResult> GoOnlineAsync(
        string userId, GoOnlineRequest request, CancellationToken ct)
    {
        var previous = await _owner.GetAvailabilityAsync(userId, ct);
        var row = await _owner.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
        {
            Online = true,
            VehicleType = request.VehicleType.ToWire(),
            Zone = request.Zone,
            Lat = request.Latitude,
            Lng = request.Longitude,
        }, userId, ct);
        return new GoOnlineResult
        {
            Availability = Map(row),
            WasAlreadyOnline = previous?.Online == true,
        };
    }

    public async Task<GoOfflineResult> GoOfflineAsync(
        string userId, GoOfflineReason reason, CancellationToken ct)
    {
        var previous = await _owner.GetAvailabilityAsync(userId, ct);
        var row = await _owner.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
        {
            Online = false,
            VehicleType = previous?.VehicleType,
            Zone = previous?.Zone,
            Lat = previous?.Lat,
            Lng = previous?.Lng,
        }, userId, ct);
        return new GoOfflineResult
        {
            Availability = Map(row),
            WasOnline = previous?.Online == true,
            // Offer withdrawal belongs to offer-service. No gateway-local ledger
            // exists from which a count could be invented.
            WithdrawnOffers = 0,
        };
    }

    public async Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        var row = await _owner.GetAvailabilityAsync(userId, ct);
        if (row is null || !row.Online || row.Lat is null || row.Lng is null) return;
        await _owner.HeartbeatAsync(userId, row.Lat.Value, row.Lng.Value, ct);
    }

    public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct) =>
        Unsupported("delivery-service online availability list");

    public Task<IReadOnlyList<JeeberAvailability>> ListKnownJeebersAsync(DateTimeOffset since, CancellationToken ct) =>
        Unsupported("delivery-service known-jeeber availability list");

    private static JeeberAvailability Map(JeeberAvailabilityUpstream row)
    {
        _ = VehicleTypeExtensions.TryParseWire(row.VehicleType, out var vehicle);
        return new JeeberAvailability
        {
            UserId = row.JeeberId,
            IsOnline = row.Online,
            VehicleType = vehicle,
            Zone = row.Zone,
            Latitude = row.Lat,
            Longitude = row.Lng,
            LastSeenAt = row.LastSeenAt,
            LastInteractionAt = row.LastSeenAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    private static Task<IReadOnlyList<JeeberAvailability>> Unsupported(string capability) =>
        Task.FromException<IReadOnlyList<JeeberAvailability>>(
            new OwnerCapabilityUnavailableException(capability));
}
