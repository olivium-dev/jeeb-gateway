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
/// <remarks>
/// OWNER-ESCALATED AC5 REVISION (Jeeb G4 follow-up — kill the OTP "half-and-half";
/// ADR follow-up: AC5 revision). The original AC5 (JEB-628) forbade the raw code
/// from ever leaving the gateway↔one-time-password hop, which made an in-app code
/// impossible. The owner has revised AC5 to: "the raw code is returned once,
/// auth-scoped, ONLY to the authenticated client (owner) of the delivery; it must
/// still never be logged." <see cref="Code"/> carries that in-app code and is
/// present ONLY for the delivery's own client. It is omitted from the JSON entirely
/// for a jeeber/other caller, so their response body is byte-for-byte the prior
/// shape.
/// </remarks>
public class OtpTriggerResponse
{
    public required string DeliveryId { get; init; }
    public required bool Triggered { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// OWNER-ESCALATED AC5 REVISION (Jeeb G4): the raw 4-digit handover code the
    /// CUSTOMER reads IN-APP, returned ONLY when the authenticated caller id equals
    /// this delivery's <c>ClientId</c> (the owner). For any other caller
    /// (jeeber/other) this is <see langword="null"/> and, via
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>,
    /// is omitted from the serialized body entirely — preserving the prior
    /// jeeber-facing contract exactly. The gateway mints this code, persists only
    /// its SHA-256 hash, and STILL dispatches the SMS (belt-and-suspenders). The
    /// revised AC5 still forbids logging the raw code anywhere.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
}

/// <summary>
/// Request body for POST /v1/deliveries/{id}/otp/verify (T-BE-019 / JEB-55).
/// Contains the 4-digit code to verify against the external one-time-password service.
///
/// <c>Code</c> is intentionally nullable so the controller owns the
/// "missing code" error shape (otp-code-required) instead of letting model
/// binding produce an RFC 9110-flavored 400. PR review B9 demands a
/// stable ProblemDetails type for QA assertions.
/// </summary>
public class OtpHandoverVerificationRequest
{
    public string? Code { get; init; }
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
