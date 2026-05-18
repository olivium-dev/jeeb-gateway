namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// The FROZEN set of RFC 7807 problem types the gateway may emit on
/// <c>/v1/auth/otp/{request,verify}</c> and <c>/v1/auth/refresh</c>.
///
/// Locked by audit comment #14764 (AC-ProblemTypeSet). The mobile counterpart
/// T-MOB-004 maps each <c>type</c> 1:1 to an ARB key — adding or renaming any
/// value in this set without coordinating with the mobile team is a breaking
/// change.
/// </summary>
public static class OtpProblemTypes
{
    /// <summary>Base URI namespace for the RFC 7807 <c>type</c> field.</summary>
    public const string BaseUri = "https://problems.jeeb.lb/auth";

    public const string InvalidOtp        = BaseUri + "/invalid_otp";
    public const string TooManyAttempts   = BaseUri + "/too_many_attempts";
    public const string InvalidCountry    = BaseUri + "/invalid_country";
    public const string RateLimited       = BaseUri + "/rate_limited";
    public const string InvalidPhone      = BaseUri + "/invalid_phone";

    /// <summary>
    /// The five canonical short codes used by both the tests' AC-ProblemTypeSet
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
        };
}
