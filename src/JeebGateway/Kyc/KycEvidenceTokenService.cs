using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JeebGateway.Kyc;

/// <summary>
/// Mints + validates the short-lived, self-authorizing token that lets an admin
/// browser render a KYC evidence image via an <c>&lt;img&gt;</c> tag (which cannot
/// send an Authorization header). The token is an HMAC-SHA256 over
/// {submissionId, slot, expiry} — unforgeable without the server secret, bound to
/// exactly one submission+slot, and ~300s lived. Mirrors the delivery/cases admin
/// evidence HMAC pattern (AdminEvidence:TokenKey) and the EarningsStatement payload
/// token; only an operator who passed kyc.review on the detail endpoint gets one.
/// </summary>
public sealed class KycEvidenceTokenService
{
    // Kept short so a leaked image URL dies quickly; the admin fetches the bytes
    // immediately after opening the detail view.
    private const int TokenTtlSeconds = 300;

    private static readonly IReadOnlySet<string> AllowedSlots =
        new HashSet<string>(StringComparer.Ordinal) { "id-front", "id-back", "selfie" };

    private readonly byte[] _key;

    public KycEvidenceTokenService(IConfiguration config)
    {
        // Reuse the already-provisioned admin-evidence secret boundary when set;
        // otherwise a per-process random key (single live instance, 300s tokens).
        var configured = config["KycEvidence:TokenKey"] ?? config["AdminEvidence:TokenKey"];
        _key = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);
    }

    public static bool IsKnownSlot(string slot) => AllowedSlots.Contains(slot);

    public (string token, DateTimeOffset expiresAt) Create(string submissionId, string slot)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(TokenTtlSeconds);
        var payload = new EvidenceTokenPayload(submissionId, slot, expiresAt.ToUnixTimeSeconds());
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return ($"{payloadB64}.{ComputeHmac(payloadB64)}", expiresAt);
    }

    public bool Validate(string? token, string submissionId, string slot)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        var expected = ComputeHmac(parts[0]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(expected)))
            return false;

        EvidenceTokenPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<EvidenceTokenPayload>(
                Encoding.UTF8.GetString(Base64UrlDecode(parts[0])))!;
        }
        catch { return false; }

        if (DateTimeOffset.FromUnixTimeSeconds(payload.Exp) < DateTimeOffset.UtcNow) return false;
        return string.Equals(payload.Sub, submissionId, StringComparison.Ordinal)
               && string.Equals(payload.Slot, slot, StringComparison.Ordinal);
    }

    private string ComputeHmac(string data) =>
        Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data)));

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var pad = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', pad));
    }

    private sealed record EvidenceTokenPayload(string Sub, string Slot, long Exp);
}
