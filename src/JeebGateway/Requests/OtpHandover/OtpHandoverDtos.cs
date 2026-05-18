namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// POST /deliveries/{id}/verify-otp body (T-backend-015 / JEEB-33).
/// The Jeeber app submits the 6-digit code the Client read out at
/// drop-off; the gateway compares it to <c>DeliveryRequest.DeliveryOtp</c>
/// under the store's write lock and either transitions the row to
/// <c>delivered</c> or increments the attempt counter.
/// </summary>
public class OtpVerificationRequest
{
    /// <summary>The OTP entered by the Jeeber. Required, non-empty.</summary>
    public string? OtpCode { get; set; }
}

/// <summary>
/// Verbose response body returned by POST /deliveries/{id}/verify-otp on
/// success or on a wrong-OTP attempt that did not lock the row out.
/// Surfaces the remaining attempt budget so the mobile UI can render
/// "2 of 3 remaining" without an extra GET.
/// </summary>
public class OtpVerificationResponse
{
    public required DeliveryRequestDto Delivery { get; init; }
    public required int AttemptsRemaining { get; init; }
    public required bool Verified { get; init; }
}

/// <summary>
/// 423 Locked response body returned when the OTP has been locked out
/// after <see cref="OtpHandoverOptions.MaxAttempts"/> wrong attempts.
/// Carries the escalation id so the mobile UI can deep-link the Client
/// into the support flow that references it.
/// </summary>
public class OtpLockedResponse
{
    public required string EscalationId { get; init; }
    public required DateTimeOffset LockedAt { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Response from GET /v1/deliveries/{id}/otp (T-BE-019 / JEB-55).
/// Confirms that a 4-digit OTP has been triggered via the external
/// one-time-password service with ApplicationId delivery_handover_{deliveryId}.
/// </summary>
public class OtpTriggerResponse
{
    public required string DeliveryId { get; init; }
    public required bool Triggered { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Request body for POST /v1/deliveries/{id}/otp/verify (T-BE-019 / JEB-55).
/// Contains the 4-digit code to verify against the external one-time-password service.
/// </summary>
public class OtpHandoverVerificationRequest
{
    public required string Code { get; init; }
}

/// <summary>
/// Response from POST /v1/deliveries/{id}/otp/verify on successful verification
/// (T-BE-019 / JEB-55). Confirms the delivery has transitioned to 'done' status.
/// </summary>
public class OtpHandoverVerificationResponse
{
    public required string DeliveryId { get; init; }
    public required bool Verified { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
}
