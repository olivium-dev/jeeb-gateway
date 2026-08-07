using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Auth.Oidc;

internal sealed record AdminOidcCorrelation(
    string State,
    string Nonce,
    string CodeVerifier,
    string ReturnPath,
    long IssuedAt,
    string? PreviousRefreshToken);

/// <summary>
/// AES-GCM protected, short-lived OIDC correlation. Keeping verifier, nonce,
/// state, and return path in one authenticated cookie avoids gateway process
/// state and works across replicas without exposing the PKCE verifier.
/// </summary>
internal static class AdminOidcCorrelationProtector
{
    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes("jeeb-admin-oidc-correlation-v1");

    public static string Protect(AdminOidcCorrelation correlation, byte[] key)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(correlation);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);

        var envelope = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, nonce.Length);
        ciphertext.CopyTo(envelope, nonce.Length + tag.Length);
        return Base64UrlEncoder.Encode(envelope);
    }

    public static bool TryUnprotect(string? value, byte[] key, out AdminOidcCorrelation? correlation)
    {
        correlation = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192) return false;
        try
        {
            var envelope = Base64UrlEncoder.DecodeBytes(value);
            if (envelope.Length <= 28) return false;
            var nonce = envelope.AsSpan(0, 12);
            var tag = envelope.AsSpan(12, 16);
            var ciphertext = envelope.AsSpan(28);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData);
            correlation = JsonSerializer.Deserialize<AdminOidcCorrelation>(plaintext);
            return correlation is not null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
    }

    public static string RandomValue(int bytes = 32) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    public static string CodeChallenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
