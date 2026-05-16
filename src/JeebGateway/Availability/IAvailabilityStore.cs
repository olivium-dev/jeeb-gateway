namespace JeebGateway.Availability;

/// <summary>
/// Coordinates the durable Jeeber availability record with the Redis
/// geo index and the offer-service withdrawal hook (T-backend-023).
///
/// In-memory implementation lives alongside; production wiring will
/// hit Postgres for the durable row, Redis for the geo + heartbeat
/// keys, and the offer-service NSwag client for withdrawals.
/// </summary>
public interface IAvailabilityStore
{
    Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct);

    Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct);

    Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct);

    /// <summary>
    /// Records a non-toggle interaction (GPS heartbeat or in-app event)
    /// that pushes the auto-offline deadline forward.
    /// </summary>
    Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Snapshot of all currently-online Jeebers. Used by the auto-offline
    /// sweeper; in production this becomes a partial-index Postgres scan.
    /// </summary>
    Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct);
}

public class GoOnlineRequest
{
    public required VehicleType VehicleType { get; init; }
    public required string Zone { get; init; }
    public double? Longitude { get; init; }
    public double? Latitude { get; init; }
}

public class GoOnlineResult
{
    public required JeeberAvailability Availability { get; init; }
    public required bool WasAlreadyOnline { get; init; }
}

public class GoOfflineResult
{
    public required JeeberAvailability Availability { get; init; }
    public required int WithdrawnOffers { get; init; }
    public required bool WasOnline { get; init; }
}

public enum GoOfflineReason
{
    UserToggle,
    AutoOfflineInactive
}
