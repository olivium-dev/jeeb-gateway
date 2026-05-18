using PhoneNumbers;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC-PhoneNorm — normalises every inbound phone to canonical E.164 with
/// region "LB" (Lebanon pilot). Wraps Google's libphonenumber via the
/// libphonenumber-csharp port so equivalent surface forms collide on the
/// same hash (AC-PhonePIIHash).
/// </summary>
public interface IPhoneNormalizer
{
    /// <summary>
    /// Attempt to parse and normalise <paramref name="rawInput"/> as a
    /// Lebanese phone number. The result classifies the input into exactly
    /// one of four outcomes:
    ///   <list type="bullet">
    ///     <item><see cref="PhoneNormalizationOutcome.Normalized"/> — parsed,
    ///       valid, RegionCode == LB.</item>
    ///     <item><see cref="PhoneNormalizationOutcome.NonLebanese"/> — parsed
    ///       but RegionCode != LB (AC-PhoneNorm → 400 invalid_country).</item>
    ///     <item><see cref="PhoneNormalizationOutcome.Unparseable"/> — could
    ///       not be parsed at all (400 invalid_phone).</item>
    ///   </list>
    /// </summary>
    PhoneNormalizationResult Normalize(string? rawInput);
}

public enum PhoneNormalizationOutcome
{
    Normalized,
    NonLebanese,
    Unparseable,
}

public readonly record struct PhoneNormalizationResult(
    PhoneNormalizationOutcome Outcome,
    string? E164,
    string? RegionCode);

public sealed class LibPhoneNumberPhoneNormalizer : IPhoneNormalizer
{
    private const string LebaneseRegion = "LB";
    private readonly PhoneNumberUtil _util = PhoneNumberUtil.GetInstance();

    public PhoneNormalizationResult Normalize(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new PhoneNormalizationResult(
                PhoneNormalizationOutcome.Unparseable, null, null);
        }

        PhoneNumber parsed;
        try
        {
            parsed = _util.Parse(rawInput, defaultRegion: LebaneseRegion);
        }
        catch (NumberParseException)
        {
            return new PhoneNormalizationResult(
                PhoneNormalizationOutcome.Unparseable, null, null);
        }

        var region = _util.GetRegionCodeForNumber(parsed);
        if (!string.Equals(region, LebaneseRegion, StringComparison.Ordinal))
        {
            // Still surface E.164 for diagnostics, but the outcome
            // remains NonLebanese — the controller MUST reject.
            var e164NonLb = _util.Format(parsed, PhoneNumberFormat.E164);
            return new PhoneNormalizationResult(
                PhoneNormalizationOutcome.NonLebanese, e164NonLb, region);
        }

        var e164 = _util.Format(parsed, PhoneNumberFormat.E164);
        return new PhoneNormalizationResult(
            PhoneNormalizationOutcome.Normalized, e164, region);
    }
}
