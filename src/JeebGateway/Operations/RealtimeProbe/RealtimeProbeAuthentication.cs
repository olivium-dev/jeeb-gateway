using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace JeebGateway.Operations.RealtimeProbe;

/// <summary>
/// Staging-only configuration for the edge-to-gateway realtime probe mint.
/// The key has one purpose: authenticate this exact descriptor request. It is
/// never accepted as a bearer, Guardian, session-JWT, or membership-ticket key.
/// </summary>
internal sealed class RealtimeProbeOptions
{
    internal const string SectionName = "Operations:RealtimeProbe";
    internal const string RequiredMintKeyFile = "/run/secrets/staging_wss_probe_mint_key";

    /// <summary>Absolute path to the dedicated, file-backed HMAC-SHA256 key.</summary>
    public string? MintKeyFile { get; set; }
}

internal enum RealtimeProbeAuthenticationStatus
{
    Authenticated,
    Malformed,
    Stale,
    Forbidden,
    Unavailable,
}

internal readonly record struct RealtimeProbeAuthentication(
    RealtimeProbeAuthenticationStatus Status,
    string? Nonce = null);

internal interface IRealtimeProbeRequestAuthenticator
{
    RealtimeProbeAuthentication Authenticate(IHeaderDictionary headers);
}

/// <summary>
/// Validates the timestamped HMAC request without ever logging header or key
/// material. The canonical message deliberately has no trailing newline.
/// </summary>
internal sealed class RealtimeProbeRequestAuthenticator : IRealtimeProbeRequestAuthenticator
{
    internal const string TimestampHeader = "X-Jeeb-Staging-Probe-Timestamp";
    internal const string NonceHeader = "X-Jeeb-Staging-Probe-Nonce";
    internal const string SignatureHeader = "X-Jeeb-Staging-Probe-Signature";
    internal const int MaximumClockSkewSeconds = 60;

    private const int MinimumKeyBytes = 32;
    private const int MaximumKeyFileBytes = 4096;

    private readonly TimeProvider _clock;
    private readonly byte[]? _key;

    public RealtimeProbeRequestAuthenticator(
        IOptions<RealtimeProbeOptions> options,
        TimeProvider clock,
        ILogger<RealtimeProbeRequestAuthenticator> logger)
    {
        _clock = clock;
        _key = TryReadKey(options.Value.MintKeyFile, logger);
    }

    public RealtimeProbeAuthentication Authenticate(IHeaderDictionary headers)
    {
        if (!TryReadSingleHeader(headers, TimestampHeader, out var timestampText)
            || !TryReadSingleHeader(headers, NonceHeader, out var nonce)
            || !TryReadSingleHeader(headers, SignatureHeader, out var signature))
        {
            return new(RealtimeProbeAuthenticationStatus.Malformed);
        }

        if (!long.TryParse(
                timestampText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var timestamp)
            || !string.Equals(
                timestampText,
                timestamp.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || timestamp < 0
            || !Guid.TryParseExact(nonce, "D", out var nonceValue)
            || !string.Equals(nonceValue.ToString("D"), nonce, StringComparison.Ordinal)
            || !IsLowercaseSha256Hex(signature))
        {
            return new(RealtimeProbeAuthenticationStatus.Malformed);
        }

        if (_key is null)
        {
            return new(RealtimeProbeAuthenticationStatus.Unavailable);
        }

        var canonical = BuildCanonical(timestampText, nonce);
        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical));
        var supplied = Convert.FromHexString(signature);
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            return new(RealtimeProbeAuthenticationStatus.Forbidden);
        }

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (timestamp < now - MaximumClockSkewSeconds
            || timestamp > now + MaximumClockSkewSeconds)
        {
            return new(RealtimeProbeAuthenticationStatus.Stale);
        }

        return new(RealtimeProbeAuthenticationStatus.Authenticated, nonce);
    }

    internal static string BuildCanonical(string timestamp, string nonce)
        => "v1\nPOST\n"
           + StagingRealtimeProbeEndpoint.Route
           + "\n"
           + timestamp
           + "\n"
           + nonce;

    private static bool TryReadSingleHeader(
        IHeaderDictionary headers,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!headers.TryGetValue(name, out var values) || values.Count != 1)
        {
            return false;
        }

        value = values[0] ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsLowercaseSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[]? TryReadKey(
        string? path,
        ILogger<RealtimeProbeRequestAuthenticator> logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            logger.LogError("Staging realtime probe mint-key file is not configured correctly.");
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < MinimumKeyBytes or > MaximumKeyFileBytes)
            {
                logger.LogError(
                    "Staging realtime probe mint-key file is unavailable or outside the allowed size.");
                return null;
            }

            var key = File.ReadAllBytes(path);
            if (key.Length is < MinimumKeyBytes or > MaximumKeyFileBytes)
            {
                CryptographicOperations.ZeroMemory(key);
                logger.LogError(
                    "Staging realtime probe mint-key file changed while being read.");
                return null;
            }

            return key;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            logger.LogError(
                "Staging realtime probe mint-key file could not be read ({FailureType}).",
                exception.GetType().Name);
            return null;
        }
    }
}
