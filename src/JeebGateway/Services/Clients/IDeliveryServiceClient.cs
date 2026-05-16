using JeebGateway.Tiers;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over delivery-service (Go) Jeeb routes (internal/jeeb/handlers.go).
/// Used by the gateway controllers when <c>FeatureFlags:UseUpstream:Delivery</c>
/// is set.
/// </summary>
public interface IDeliveryServiceClient
{
    Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct);

    Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct);

    Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct);

    Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct);

    Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct);
}

public sealed class CreateDeliveryRequestUpstream
{
    public required string ClientId { get; init; }
    public required string Description { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public required string TierId { get; init; }
    public required LatLngUpstream Pickup { get; init; }
    public required LatLngUpstream Dropoff { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
}

public sealed class LatLngUpstream
{
    public required double Lat { get; init; }
    public required double Lng { get; init; }
}

public sealed class DeliveryRequestUpstream
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string Status { get; init; }
    public string? Description { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public string? TierId { get; init; }
    public LatLngUpstream? Pickup { get; init; }
    public LatLngUpstream? Dropoff { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
    public string? JeeberId { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public bool GpsTrackingActive { get; init; }
    public int OtpAttemptCount { get; init; }
    public DateTimeOffset? OtpLockedAt { get; init; }
    public string? OtpEscalationId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? CancelledBy { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DeliveryOtpVerifyResult
{
    public DeliveryRequestUpstream? Request { get; init; }
    public bool Verified { get; init; }
    public int AttemptsRemaining { get; init; }
    public string? Reason { get; init; }
    public string? EscalationId { get; init; }
    public DateTimeOffset? LockedAt { get; init; }
}

public sealed class DeliveryCancelUpstreamRequest
{
    public required string Role { get; init; }
    public required string UserId { get; init; }
    public string? Reason { get; init; }
}

public sealed class DeliveryCancelResult
{
    public required DeliveryRequestUpstream Request { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
}

public sealed class JeeberAvailabilityUpstreamRequest
{
    public required bool Online { get; init; }
    public string? VehicleType { get; init; }
    public string? Zone { get; init; }
    public double? Lat { get; init; }
    public double? Lng { get; init; }
}

public sealed class JeeberAvailabilityUpstream
{
    public required string JeeberId { get; init; }
    public required bool Online { get; init; }
    public string? VehicleType { get; init; }
    public string? Zone { get; init; }
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
