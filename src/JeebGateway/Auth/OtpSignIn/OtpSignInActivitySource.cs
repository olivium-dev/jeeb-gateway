using System.Diagnostics;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC6 — dedicated OTel <see cref="ActivitySource"/> for the OTP sign-in path.
/// Span names: <c>auth.otp.request</c>, <c>auth.otp.verify</c>, <c>auth.refresh</c>.
/// Every span carries <c>phone.hash</c> (bcrypt) but NEVER raw E.164
/// (AC-PhonePIIHash). The source name is registered in <see cref="JeebGateway"/>'s
/// OTel tracer provider so tests can subscribe via <c>tracing.AddSource(...)</c>.
/// </summary>
public static class OtpSignInActivitySource
{
    public const string Name = "JeebGateway.Auth.OtpSignIn";

    public static readonly ActivitySource Source = new(Name, "1.0.0");

    public const string SpanRequest = "auth.otp.request";
    public const string SpanVerify  = "auth.otp.verify";
    public const string SpanRefresh = "auth.refresh";

    // Tag keys — kept as constants so they can be asserted in tests.
    public const string TagPhoneHash         = "phone.hash";
    public const string TagPhoneNorm         = "phone.normalized";   // bool: did normalization succeed?
    public const string TagPhoneRegion       = "phone.region";       // RegionCode (LB / US / ...)
    public const string TagOtpOutcome        = "auth.otp.outcome";   // ok | invalid_otp | too_many_attempts | invalid_country | rate_limited | invalid_phone
    public const string TagDownstreamReused  = "auth.otp.downstream.reused";
    public const string TagUserIsNew         = "auth.user.is_new";
    public const string TagRefreshOutcome    = "auth.refresh.outcome";
}
