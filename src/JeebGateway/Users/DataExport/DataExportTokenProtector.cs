using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Users.DataExport;

public interface IDataExportTokenProtector
{
    DataExportCapability Create(Guid workItemId);
    bool TryValidate(string token, out DataExportCapability capability);
}

public sealed record DataExportCapability(Guid WorkItemId, string Token, string TokenHash);

/// <summary>
/// Deterministic capability tokens let a stateless gateway reconstruct the
/// user's ready-link from the work id. Only a SHA-256 hash is persisted by
/// state-service; the mounted HMAC key and raw token never leave the gateway.
/// </summary>
public sealed class DataExportTokenProtector(IConfiguration configuration) : IDataExportTokenProtector
{
    public const string SigningKeyFileKey = "DATA_EXPORT_TOKEN_SIGNING_KEY_FILE";
    private const string Version = "v1";

    public DataExportCapability Create(Guid workItemId)
    {
        var id = workItemId.ToString("N");
        var signature = Sign(id, ReadKey());
        var token = $"{Version}.{id}.{Base64Url(signature)}";
        return new DataExportCapability(workItemId, token, Hash(token));
    }

    public bool TryValidate(string token, out DataExportCapability capability)
    {
        capability = null!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 500)
            return false;
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || parts[1].Length != 32
            || !Guid.TryParseExact(parts[1], "N", out var workItemId))
            return false;

        byte[] supplied;
        try
        {
            supplied = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Sign(parts[1], ReadKey());
        if (supplied.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            return false;

        capability = new DataExportCapability(workItemId, token, Hash(token));
        return true;
    }

    private byte[] ReadKey()
    {
        var path = configuration[SigningKeyFileKey];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException(
                $"{SigningKeyFileKey} must be an absolute mounted-secret path.");

        string key;
        try
        {
            key = File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Data-export token signing key is unavailable.", ex);
        }
        if (key.Length < 32 || key.Length > 4096 || key.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Data-export token signing key is invalid.");
        return Encoding.UTF8.GetBytes(key);
    }

    private static byte[] Sign(string id, byte[] key) =>
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"jeeb-data-export:{Version}:{id}"));

    private static string Hash(string token) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64URL length.")
        };
        return Convert.FromBase64String(base64);
    }
}
