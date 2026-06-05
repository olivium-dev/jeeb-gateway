using PhoneNumbers;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Gateway-local phone admission policy for the sign-in OTP surface (S02 F-E,
/// JEB-37 / JEB-1422). Runs entirely in the gateway BEFORE the
/// <c>one-time-password</c> upstream is dialed — there is NO upstream change.
/// The shared service stays a generic OTP primitive; the Lebanon-only product
/// rule and the E.164 reject live here, in the BFF, exactly where the Jeeb
/// product vocabulary belongs.
///
/// <para>Two reject outcomes, both surfaced by <see cref="AuthOtpController"/> as
/// RFC 7807 <c>application/problem+json</c> under the frozen
/// <c>https://problems.jeeb.lb/auth</c> base:</para>
/// <list type="bullet">
///   <item><see cref="PhonePolicyOutcome.InvalidPhone"/> — E.164 parse failure
///   (<c>400 invalid_phone</c>); the number is not a dialable phone at all.</item>
///   <item><see cref="PhonePolicyOutcome.InvalidCountry"/> — parses, but the
///   region is not the allowed region (<c>400 invalid_country</c>); for Jeeb the
///   allowed region is Lebanon (LB).</item>
/// </list>
///
/// <para>Parse-first ordering is deliberate: a syntactically broken number
/// (e.g. <c>+961ABC</c>) is <c>invalid_phone</c>, not <c>invalid_country</c> —
/// the gateway cannot assert a region for a number it cannot parse (S02 N4 vs
/// N3 are distinct asserts).</para>
/// </summary>
public interface IPhonePolicy
{
    /// <summary>
    /// Classifies <paramref name="rawPhone"/> against the configured allowed
    /// region. Returns <see cref="PhonePolicyOutcome.Allowed"/> when the number
    /// parses AND its region is allowed; otherwise the specific reject reason.
    /// </summary>
    PhonePolicyResult Evaluate(string? rawPhone);
}

public enum PhonePolicyOutcome
{
    Allowed,
    InvalidPhone,
    InvalidCountry,
}

public readonly record struct PhonePolicyResult(PhonePolicyOutcome Outcome)
{
    public bool IsAllowed => Outcome == PhonePolicyOutcome.Allowed;

    public static readonly PhonePolicyResult Allowed = new(PhonePolicyOutcome.Allowed);
    public static readonly PhonePolicyResult InvalidPhone = new(PhonePolicyOutcome.InvalidPhone);
    public static readonly PhonePolicyResult InvalidCountry = new(PhonePolicyOutcome.InvalidCountry);
}

/// <summary>
/// Options for <see cref="PhonePolicy"/>. Bound from <c>Auth:Otp:Phone</c>.
/// The allowed region is configuration (default LB) so a future market can be
/// added without a code change — the Jeeb-specific value is data, the
/// libphonenumber mechanism is generic.
/// </summary>
public sealed class PhonePolicyOptions
{
    public const string SectionName = "Auth:Otp:Phone";

    /// <summary>
    /// ISO-3166-1 alpha-2 region code admitted for sign-in. Defaults to Lebanon.
    /// A number whose national region differs is rejected with
    /// <c>invalid_country</c>.
    /// </summary>
    public string AllowedRegion { get; set; } = "LB";

    /// <summary>
    /// When false the policy admits every parseable E.164 number regardless of
    /// region (still rejects unparseable numbers as <c>invalid_phone</c>). Lets a
    /// non-production environment relax the LB gate without touching code.
    /// Defaults to true (enforce the region gate).
    /// </summary>
    public bool EnforceRegion { get; set; } = true;
}

/// <inheritdoc />
public sealed class PhonePolicy : IPhonePolicy
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    private readonly string _allowedRegion;
    private readonly bool _enforceRegion;

    public PhonePolicy(Microsoft.Extensions.Options.IOptions<PhonePolicyOptions> options)
    {
        var o = options.Value;
        _allowedRegion = string.IsNullOrWhiteSpace(o.AllowedRegion)
            ? "LB"
            : o.AllowedRegion.Trim().ToUpperInvariant();
        _enforceRegion = o.EnforceRegion;
    }

    public PhonePolicyResult Evaluate(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
        {
            // No phone at all is an invalid_phone — there is nothing to parse.
            return PhonePolicyResult.InvalidPhone;
        }

        PhoneNumber parsed;
        try
        {
            // Parse against the allowed region as the default so national-format
            // numbers are still classified; an explicit +<cc> prefix overrides it.
            parsed = Util.Parse(rawPhone, _allowedRegion);
        }
        catch (NumberParseException)
        {
            return PhonePolicyResult.InvalidPhone;
        }

        // Libphonenumber's own validity check: rejects e.g. wrong-length numbers
        // that "parse" structurally but are not real dialable numbers.
        if (!Util.IsValidNumber(parsed))
        {
            return PhonePolicyResult.InvalidPhone;
        }

        if (!_enforceRegion)
        {
            return PhonePolicyResult.Allowed;
        }

        var region = Util.GetRegionCodeForNumber(parsed);
        if (!string.Equals(region, _allowedRegion, StringComparison.OrdinalIgnoreCase))
        {
            return PhonePolicyResult.InvalidCountry;
        }

        return PhonePolicyResult.Allowed;
    }
}
