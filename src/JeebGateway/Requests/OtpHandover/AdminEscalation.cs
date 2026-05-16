namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Why a delivery was escalated to an admin (T-backend-015).
/// </summary>
public static class EscalationReason
{
    /// <summary>OTP lockout after <c>OtpHandoverOptions.MaxAttempts</c> wrong submissions.</summary>
    public const string OtpLocked = "otp_locked";

    /// <summary>Client was flagged unreachable and the timer elapsed without resolution.</summary>
    public const string ClientUnreachable = "client_unreachable";
}

/// <summary>
/// Lifecycle status of an admin escalation row. The MVP only opens
/// escalations; the admin moderation surface (T-backend-052) will flip
/// them to <see cref="Resolved"/>.
/// </summary>
public static class EscalationStatus
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
}

/// <summary>
/// One admin escalation row produced by the OTP handover flow
/// (T-backend-015 / JEEB-33). Stored in <c>IAdminEscalationStore</c>;
/// production wiring lands a Postgres-backed table colocated with
/// <c>admin_actions</c> in db/migrations/0005.
/// </summary>
public sealed class AdminEscalation
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }
    public required string ClientId { get; init; }
    public string? JeeberId { get; init; }
    public required string Reason { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Snapshot of <c>DeliveryRequest.OtpAttemptCount</c> at the moment
    /// the escalation was opened. Only meaningful for
    /// <see cref="EscalationReason.OtpLocked"/> rows — included so
    /// admins can tell apart "lockout from 3 fast retries" from
    /// "lockout after several legitimate attempts spread over hours".
    /// </summary>
    public int OtpAttemptCount { get; init; }
}
