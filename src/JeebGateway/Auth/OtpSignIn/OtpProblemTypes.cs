namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// The FROZEN set of RFC 7807 problem types the gateway may emit on
/// <c>/v1/auth/otp/{request,verify}</c> and <c>/v1/auth/refresh</c>.
///
/// Locked by audit comment #14764 (AC-ProblemTypeSet). The mobile counterpart
/// T-MOB-004 maps each <c>type</c> 1:1 to an ARB key — adding or renaming any
/// value in this set without coordinating with the mobile team is a breaking
/// change.
///
/// PR #32 review extensions (additive, coordinated with T-MOB-004):
///   - S1: <c>service_unavailable</c> — replaces the ad-hoc <c>/downstream</c>
///     and <c>/user_mgmt_unavailable</c> URIs the controller previously emitted
///     for 502/503 technical failures. Single mobile copy entry covers both.
///   - S3: <c>invalid_refresh_token</c> — distinct from <c>invalid_otp</c> so
///     the mobile client renders a "session expired, please sign in again"
///     copy instead of "wrong code" when a stolen / expired refresh token is
///     presented.
/// </summary>
public static class OtpProblemTypes
{
    /// <summary>Base URI namespace for the RFC 7807 <c>type</c> field.</summary>
    public const string BaseUri = "https://problems.jeeb.lb/auth";

    public const string InvalidOtp           = BaseUri + "/invalid_otp";
    public const string TooManyAttempts      = BaseUri + "/too_many_attempts";
    public const string InvalidCountry       = BaseUri + "/invalid_country";
    public const string RateLimited          = BaseUri + "/rate_limited";
    public const string InvalidPhone         = BaseUri + "/invalid_phone";
    // PR #32 review S1 — consolidated 502/503 technical failure marker.
    public const string ServiceUnavailable   = BaseUri + "/service_unavailable";
    // PR #32 review S3 — distinct from InvalidOtp so mobile maps to the
    // "session expired" copy on a bad / stolen refresh token.
    public const string InvalidRefreshToken  = BaseUri + "/invalid_refresh_token";

    /// <summary>
    /// The canonical short codes used by both the tests' AC-ProblemTypeSet
    /// guard and the mobile ARB-key mapping. The full URI must END WITH one of
    /// these — never invent new types.
    /// </summary>
    public static readonly IReadOnlySet<string> FrozenSet =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "invalid_otp",
            "too_many_attempts",
            "invalid_country",
            "rate_limited",
            "invalid_phone",
            "service_unavailable",
            "invalid_refresh_token",
        };
}
