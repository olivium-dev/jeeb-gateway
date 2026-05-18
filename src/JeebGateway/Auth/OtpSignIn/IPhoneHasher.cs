using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC-PhonePIIHash — produces a DETERMINISTIC, peppered HMAC-SHA256 of the
/// normalised E.164 phone number. This is the only observable form of a phone
/// number in structured logs, OTel span attributes, metrics, and downstream
/// span attributes. Raw E.164 phone numbers MUST never leave the controller
/// scope.
///
/// Why HMAC-SHA256 (not bcrypt): the prior implementation used
/// <c>BCrypt.HashPassword(phone, 12)</c> which generates a fresh random salt
/// on every call, so two requests from the same phone produced different
/// <c>phone.hash</c> values. The span tag and JWT <c>phone_hash</c> claim were
/// effectively per-request randoms, not correlation keys (PR #32 review B1).
///
/// HMAC-SHA256 with a static server-side pepper (loaded from
/// <c>JeebJwt:PhonePepper</c> env / sealed secret) gives:
///   <list type="bullet">
///     <item>Determinism — same input → same output, so log/span correlation
///       across requests from one phone works.</item>
///     <item>Pre-image resistance — without the pepper, the hash is just an
///       opaque token; with the pepper, even a leaked log dump cannot be
///       brute-forced against the +961xxxxxxxx keyspace (the pepper is not
///       in the same store as the logs).</item>
///   </list>
/// </summary>
public interface IPhoneHasher
{
    /// <summary>
    /// Hash an already-normalised E.164 phone number using HMAC-SHA256 with
    /// the org-pinned pepper. Equivalent input formats must be normalised
    /// BEFORE calling this method — see <see cref="IPhoneNormalizer"/>.
    /// </summary>
    string HashE164(string normalizedE164);
}

public sealed class HmacShaPhoneHasher : IPhoneHasher, IDisposable
{
    public const string HashPrefix = "ph1:";

    private readonly HMACSHA256 _hmac;

    public HmacShaPhoneHasher(IOptions<JeebJwtOptions> options)
    {
        var pepper = options.Value.PhonePepper;
        if (string.IsNullOrWhiteSpace(pepper))
        {
            // The pepper MUST come from env / sealed secret; the data-annotation
            // [Required] check on JeebJwtOptions.PhonePepper trips on startup if
            // the binding is missing. The defensive check here covers the
            // (test-only) path where Options.Create is used without binding.
            throw new InvalidOperationException(
                "JeebJwt:PhonePepper must be configured (env / sealed secret). " +
                "See JeebJwtOptions.PhonePepper.");
        }
        _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
    }

    public string HashE164(string normalizedE164)
    {
        if (string.IsNullOrWhiteSpace(normalizedE164))
        {
            throw new ArgumentException(
                "normalizedE164 must be non-empty; call IPhoneNormalizer first.",
                nameof(normalizedE164));
        }

        var bytes = Encoding.UTF8.GetBytes(normalizedE164);
        var mac   = _hmac.ComputeHash(bytes);
        // base64url so the value is safe in URLs, log lines, and span tags.
        return HashPrefix + Base64UrlEncode(mac);
    }

    public void Dispose() => _hmac.Dispose();

    private static string Base64UrlEncode(byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
