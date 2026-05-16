namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Outcome of <see cref="IRequestsStore.TryVerifyOtpAsync"/>. The
/// controller pattern-matches to choose between 200, 400, 404, and 423.
/// </summary>
public enum OtpVerificationOutcome
{
    /// <summary>OTP matched — row transitioned to <c>delivered</c>.</summary>
    Verified,

    /// <summary>The delivery id is unknown.</summary>
    NotFound,

    /// <summary>The row is not in <see cref="RequestStatus.HeadingOff"/> so OTP verification is not allowed.</summary>
    NotInHandoverState,

    /// <summary>The OTP did not match and the row still has attempts left.</summary>
    Mismatch,

    /// <summary>This call (or a prior call) consumed the last allowed attempt — the OTP is now locked.</summary>
    Locked,
}

/// <summary>
/// Bundle returned by <see cref="IRequestsStore.TryVerifyOtpAsync"/>.
/// <list type="bullet">
///   <item><see cref="JustLockedOut"/> is true on the single call that flipped
///     the row from "attempts remaining" to "locked" — the controller uses
///     this edge to create the escalation row exactly once.</item>
///   <item><see cref="AttemptsRemaining"/> is the live budget after this
///     attempt; 0 when the row is locked.</item>
/// </list>
/// </summary>
public sealed record OtpVerificationResult(
    OtpVerificationOutcome Outcome,
    DeliveryRequest? Request,
    int AttemptsRemaining,
    bool JustLockedOut);
