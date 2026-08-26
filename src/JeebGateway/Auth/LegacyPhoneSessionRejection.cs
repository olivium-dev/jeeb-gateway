using JeebGateway.Observability;
using JeebGateway.Tokens;

namespace JeebGateway.Auth;

/// <summary>
/// Identifies and retires the phone-number subjects minted by the historical
/// fail-open OTP identity fallback. This is deliberately a subject classifier,
/// not phone-number admission or normalization policy.
/// </summary>
internal static class LegacyPhoneSessionRejection
{
    /// <summary>
    /// Matches only compact ASCII E.164-shaped subjects: a leading <c>+</c>, a
    /// non-zero first digit, and 8-15 total digits. No normalization is performed.
    /// </summary>
    internal static bool IsLegacySubject(string? subject)
    {
        if (subject is null || subject.Length is < 9 or > 16 || subject[0] != '+')
            return false;

        if (subject[1] is < '1' or > '9')
            return false;

        for (var index = 2; index < subject.Length; index++)
        {
            if (subject[index] is < '0' or > '9')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort, idempotent retirement of every refresh family for the exact
    /// stored subject. Callers must reject the session regardless of this method's
    /// outcome, including storage faults and cancellation.
    /// </summary>
    internal static async Task RevokeRefreshFamiliesAsync(
        IRefreshTokenStore store,
        string subject,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.RevokeAllForUserAsync(
                subject, RevocationReason.LegacyPhoneSubject, cancellationToken);
        }
        catch (Exception)
        {
            // Security invariant: a revocation-store fault must never turn a
            // retired legacy session into an accepted access or refresh token.
            // Record only a bounded reason; the subject and token stay private.
            BusinessOutcomeTelemetry.RecordLegacySessionRejection(
                LegacySessionRejectionReason.RevocationFailure);
        }
    }
}
