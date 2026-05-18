namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Thin domain contract for the <c>olivium-dev/one-time-password</c>
/// downstream service consumed by the OTP sign-in path. The contract is
/// intentionally minimal — it carries only the operations and shapes the
/// gateway controller needs, NOT the full downstream surface.
///
/// Production wiring registers the NSwag-generated <c>ServiceOTPClient</c>
/// (see <c>contracts/one-time-password-openapi.json</c> +
/// <c>scripts/regenerate-clients.sh</c>) behind an adapter that fulfils
/// this interface. Tests replace the registration with a stateful in-memory
/// fake mirroring the downstream invariants pinned by audit comment #14769:
///   <list type="bullet">
///     <item>5-minute TTL (300s) — read from downstream <c>ExpiresAt</c>.</item>
///     <item>Hard 3-attempt cap, NO time window; counter resets only on Resend.</item>
///     <item>60-second idempotency window per <c>(phone, purpose)</c>.</item>
///   </list>
/// </summary>
public interface IServiceOtpClient
{
    /// <summary>
    /// Request the downstream service issue an OTP for <paramref name="normalizedE164Phone"/>
    /// with <paramref name="purpose"/> = "login" for the OTP sign-in path.
    /// Identical (phone, purpose) within 60 s short-circuits to the existing
    /// active OTP per the downstream's idempotency behaviour.
    /// </summary>
    Task<SendOtpResult> SendAsync(string normalizedE164Phone, string purpose, CancellationToken ct = default);

    /// <summary>
    /// Validate <paramref name="code"/> against the most recent active OTP for
    /// <paramref name="normalizedE164Phone"/>. Returns <see cref="ValidateOtpResult.Ok"/>,
    /// <see cref="ValidateOtpResult.InvalidOtp"/> (wrong code or expired), or
    /// <see cref="ValidateOtpResult.TooManyAttempts"/> (≥ 3 wrong attempts).
    /// </summary>
    Task<ValidateOtpResult> ValidateAsync(string normalizedE164Phone, string code, CancellationToken ct = default);
}

/// <summary>
/// Downstream send-OTP outcome. <see cref="Reused"/> indicates the downstream
/// short-circuited via its 60s idempotency window.
/// </summary>
public sealed record SendOtpResult(string Code, DateTimeOffset ExpiresAt, bool Reused);

/// <summary>
/// Downstream validate-OTP outcome. <see cref="ProblemType"/> is the short
/// problem-type code (NOT a full URI) — one of <see cref="OtpProblemTypes.FrozenSet"/>.
/// </summary>
public sealed record ValidateOtpResult(bool Success, string? ProblemType)
{
    public static ValidateOtpResult Ok()              => new(true,  null);
    public static ValidateOtpResult InvalidOtp()      => new(false, "invalid_otp");
    public static ValidateOtpResult TooManyAttempts() => new(false, "too_many_attempts");
}
