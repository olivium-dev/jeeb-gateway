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

    /// <summary>
    /// T-BE-019 (JEB-55): the durable AtDoor-gate half of the handover OTP.
    /// Calls the frozen delivery-service contract
    /// <c>POST /api/v1/deliveries/{id}/otp/issue</c>. delivery-service owns
    /// the at_door gate: the gateway must call this BEFORE asking
    /// one-time-password to dispatch the SMS, so a courier who is not
    /// physically at the door never triggers an OTP. The raw code never
    /// leaves the gateway↔one-time-password hop (AC5) — only an optional
    /// <paramref name="codeHash"/> for support is forwarded.
    /// </summary>
    /// <returns><see cref="DeliveryHandoverIssueResult"/> on 200.</returns>
    /// <exception cref="DeliveryHandoverException">
    /// Thrown for 409 <c>not_at_door</c> and 404 so the controller maps the
    /// upstream status straight through as RFC 7807.
    /// </exception>
    Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct);

    /// <summary>
    /// T-BE-019 (JEB-55): the durable attempt-counter / 423-lock / settlement
    /// half of the handover OTP. Calls the frozen delivery-service contract
    /// <c>POST /api/v1/deliveries/{id}/otp/verify</c> with ONLY a success
    /// boolean (the gateway already validated the raw code against
    /// one-time-password; AC5 — the code never reaches delivery-service).
    /// delivery-service runs the AtDoor→Done transition + single-tx
    /// settlement on success and owns the durable, multi-replica-safe
    /// attempt counter and lock.
    /// </summary>
    /// <returns><see cref="DeliveryHandoverVerifyResult"/> on 200 (verified).</returns>
    /// <exception cref="DeliveryHandoverException">
    /// Thrown for 401 <c>invalid_code</c> (with <c>attempts_remaining</c>),
    /// 423 <c>locked</c> (with <c>escalation_id</c>), 409 <c>not_at_door</c>,
    /// and 404 so the controller maps the upstream status straight through
    /// as RFC 7807.
    /// </exception>
    Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, CancellationToken ct);

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

/// <summary>
/// T-BE-019 (JEB-55): 200 body of <c>POST /api/v1/deliveries/{id}/otp/issue</c>
/// — <c>{ delivery_id, issued:true }</c>.
/// </summary>
public sealed class DeliveryHandoverIssueResult
{
    public required string DeliveryId { get; init; }
    public bool Issued { get; init; }
}

/// <summary>
/// T-BE-019 (JEB-55): 200 body of <c>POST /api/v1/deliveries/{id}/otp/verify</c>
/// — <c>{ delivery_id, verified:true, status:"Done" }</c>.
/// </summary>
public sealed class DeliveryHandoverVerifyResult
{
    public required string DeliveryId { get; init; }
    public bool Verified { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// T-BE-019 (JEB-55): a non-200 outcome from the frozen delivery-service
/// handover-OTP endpoints. The gateway is a thin BFF on this path — it does
/// NOT re-interpret the durable gate; it surfaces the upstream
/// <see cref="StatusCode"/> + <see cref="Reason"/> back to the caller as
/// RFC 7807 ProblemDetails.
///
/// Carries the contract's typed extension fields so the controller can echo
/// them: <see cref="AttemptsRemaining"/> for 401 <c>invalid_code</c> and
/// <see cref="EscalationId"/> for 423 <c>locked</c>.
/// </summary>
public sealed class DeliveryHandoverException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }
    public int? AttemptsRemaining { get; }
    public string? EscalationId { get; }

    public DeliveryHandoverException(
        int statusCode,
        string? reason,
        int? attemptsRemaining = null,
        string? escalationId = null)
        : base($"delivery-service handover returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
        AttemptsRemaining = attemptsRemaining;
        EscalationId = escalationId;
    }
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
