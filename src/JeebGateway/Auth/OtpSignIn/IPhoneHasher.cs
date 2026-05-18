namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC-PhonePIIHash — produces <c>bcrypt(phone, workFactor=12)</c>, the only
/// observable form of a phone number in structured logs, OTel span attributes,
/// metrics, and downstream span attributes. Raw E.164 phone numbers MUST
/// never leave the controller scope.
/// </summary>
public interface IPhoneHasher
{
    /// <summary>
    /// Hash an already-normalised E.164 phone number using BCrypt with the
    /// org-pinned work factor (12). Equivalent input formats must be
    /// normalised BEFORE calling this method — see <see cref="IPhoneNormalizer"/>.
    /// </summary>
    string HashE164(string normalizedE164);
}

public sealed class BcryptPhoneHasher : IPhoneHasher
{
    private const int WorkFactor = 12;

    public string HashE164(string normalizedE164)
    {
        if (string.IsNullOrWhiteSpace(normalizedE164))
        {
            throw new ArgumentException(
                "normalizedE164 must be non-empty; call IPhoneNormalizer first.",
                nameof(normalizedE164));
        }

        // BCrypt.Net-Next 4.x — generates a fresh salt per call. The resulting
        // hash carries the salt + work factor in the leading prefix
        // "$2[ab]$12$..." which the AC-PhonePIIHash bcrypt-presence assertion
        // detects.
        return BCrypt.Net.BCrypt.HashPassword(normalizedE164, workFactor: WorkFactor);
    }
}
