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

    /// <summary>
    /// Reads shipments from the delivery-service DB via
    /// <c>GET /api/v1/shipments</c>. All parameters are optional.
    /// </summary>
    Task<ShipmentsListDto> ListShipmentsAsync(
        string? orderId,
        string? stage,
        int? limit,
        CancellationToken ct);

    Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct);

    Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct);

    /// <summary>
    /// T-BE-019 (JEB-55): canonical status transition. Delegates the
    /// status flip to the source-of-truth delivery-service so handover-OTP
    /// success can mark the delivery as 'done' there (commission settlement
    /// in T-BE-020 keys off the upstream record). The gateway must NEVER
    /// write status directly to its local store on the OTP-verified path —
    /// that splits the canonical write between two systems.
    /// </summary>
    Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct);

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

    /// <summary>
    /// T-BE-019 (JEB-55): E.164 phone for the 4-digit handover OTP.
    /// </summary>
    public string? RecipientPhone { get; init; }
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

/// <summary>
/// Maps the delivery-service <c>GET /api/v1/shipments</c> response envelope.
/// Matches the Go struct <c>ShipmentsListResponse</c> in
/// <c>delivery-service/internal/api/models.go</c>.
/// </summary>
public sealed class ShipmentsListDto
{
    public IReadOnlyList<ShipmentDetailDto> Shipments { get; init; } = Array.Empty<ShipmentDetailDto>();
    public int Count { get; init; }
}

/// <summary>
/// Maps a single element from the <c>shipments</c> array returned by
/// delivery-service. Only the fields the gateway exposes downstream are
/// represented here; unmapped fields are silently dropped by STJ.
/// </summary>
public sealed class ShipmentDetailDto
{
    public required string Id { get; init; }
    public string? TenantId { get; init; }
    public string? OrderId { get; init; }
    public string? WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public required string CurrentStage { get; init; }
    public DateTimeOffset StageEnteredAt { get; init; }
    public string? CarrierName { get; init; }
    public string? CarrierTrackingId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
