using PhoneNumbers;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Gateway-local E.164 admission and canonicalisation policy for the public
/// sign-in OTP surface. Runs before the <c>one-time-password</c> upstream is
/// dialed; the shared service remains an unmodified generic OTP primitive.
///
/// <para>Every admitted value must carry an explicit <c>+</c> country code.
/// National-format guessing, international-prefix repair, and digit truncation
/// are deliberately absent. Harmless presentation separators are removed, then
/// libphonenumber validates the exact digit sequence and emits the single E.164
/// value used by all downstream keys.</para>
///
/// <para><see cref="PhonePolicyOutcome.InvalidCountry"/> remains in the result
/// contract so older gateway integrations keep their typed
/// <c>invalid_country</c> compatibility. International eligibility is the
/// default; the outcome is emitted only when the emergency region-restriction
/// switch is enabled.</para>
/// </summary>
public interface IPhonePolicy
{
    /// <summary>
    /// Validates and canonicalises <paramref name="rawPhone"/>. An allowed
    /// result always carries a non-empty <see cref="PhonePolicyResult.CanonicalPhone"/>.
    /// </summary>
    PhonePolicyResult Evaluate(string? rawPhone);
}

public enum PhonePolicyOutcome
{
    Allowed,
    InvalidPhone,
    InvalidCountry,
}

public readonly record struct PhonePolicyResult(
    PhonePolicyOutcome Outcome,
    string? CanonicalPhone = null)
{
    public bool IsAllowed =>
        Outcome == PhonePolicyOutcome.Allowed
        && !string.IsNullOrWhiteSpace(CanonicalPhone);

    public static PhonePolicyResult Allow(string canonicalPhone) =>
        new(PhonePolicyOutcome.Allowed, canonicalPhone);

    public static readonly PhonePolicyResult InvalidPhone =
        new(PhonePolicyOutcome.InvalidPhone);

    public static readonly PhonePolicyResult InvalidCountry =
        new(PhonePolicyOutcome.InvalidCountry);
}

/// <summary>
/// Options bound from <c>Auth:Otp:Phone</c>. International eligibility is the
/// default. Operators may temporarily enable a single-region restriction as an
/// emergency fraud-containment switch; normal client country selection remains
/// independent and defaults to Lebanon in the Jeeb UI.
/// </summary>
public sealed class PhonePolicyOptions
{
    public const string SectionName = "Auth:Otp:Phone";

    public string AllowedRegion { get; set; } = "LB";

    public bool EnforceRegion { get; set; }
}

/// <inheritdoc />
public sealed class PhonePolicy : IPhonePolicy
{
    private const int MaxRawPhoneLength = 32;
    private const int MaxE164Digits = 15;
    private const string UnknownRegion = "ZZ";
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    private readonly string _allowedRegion;
    private readonly bool _enforceRegion;

    public PhonePolicy(Microsoft.Extensions.Options.IOptions<PhonePolicyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value;
        _allowedRegion = string.IsNullOrWhiteSpace(configured.AllowedRegion)
            ? "LB"
            : configured.AllowedRegion.Trim().ToUpperInvariant();
        _enforceRegion = configured.EnforceRegion;
    }

    public PhonePolicyResult Evaluate(string? rawPhone)
    {
        if (rawPhone is null
            || rawPhone.Length > MaxRawPhoneLength
            || string.IsNullOrWhiteSpace(rawPhone))
        {
            return PhonePolicyResult.InvalidPhone;
        }

        var explicitPhone = CompactExplicitInternational(rawPhone);
        if (explicitPhone is null)
        {
            return PhonePolicyResult.InvalidPhone;
        }

        PhoneNumber parsed;
        try
        {
            // UnknownRegion prevents libphonenumber from guessing any national
            // default. The compact input must already carry its country code.
            parsed = Util.Parse(explicitPhone, UnknownRegion);
        }
        catch (NumberParseException)
        {
            return PhonePolicyResult.InvalidPhone;
        }

        if (!Util.IsValidNumber(parsed))
        {
            return PhonePolicyResult.InvalidPhone;
        }

        var canonical = Util.Format(parsed, PhoneNumberFormat.E164);

        // Formatting may be removed, but no digit may be repaired, dropped, or
        // rewritten by the parser. This also fails closed on trunk-prefix and
        // overlong inputs that a permissive parser could otherwise normalise.
        if (!string.Equals(canonical, explicitPhone, StringComparison.Ordinal))
        {
            return PhonePolicyResult.InvalidPhone;
        }

        if (_enforceRegion)
        {
            var region = Util.GetRegionCodeForNumber(parsed);
            if (!string.Equals(region, _allowedRegion, StringComparison.OrdinalIgnoreCase))
            {
                return PhonePolicyResult.InvalidCountry;
            }
        }

        return PhonePolicyResult.Allow(canonical);
    }

    private static string? CompactExplicitInternational(string rawPhone)
    {
        var value = rawPhone.Trim();
        if (value.Length < 3 || value[0] != '+')
        {
            return null;
        }

        Span<char> compact = stackalloc char[MaxE164Digits + 1];
        compact[0] = '+';
        var digitCount = 0;

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= '0' and <= '9')
            {
                if (digitCount == MaxE164Digits)
                {
                    return null;
                }

                compact[++digitCount] = character;
                continue;
            }

            if (!IsPresentationSeparator(character))
            {
                return null;
            }
        }

        if (digitCount < 2 || compact[1] == '0')
        {
            return null;
        }

        return new string(compact[..(digitCount + 1)]);
    }

    private static bool IsPresentationSeparator(char character) =>
        character is ' ' or '-' or '(' or ')' or '.';
}
