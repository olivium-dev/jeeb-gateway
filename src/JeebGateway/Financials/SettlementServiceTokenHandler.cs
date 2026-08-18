using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// Attaches the SERVICE-scope bearer token settlement-service expects (NotificationServiceTokenHandler
/// shape). The admin scope — batches, mark-paid, diagnostics — is deliberately never configured here:
/// a leaked gateway token must not be able to pay anyone.
/// </summary>
public sealed class SettlementServiceTokenHandler : DelegatingHandler
{
    internal const int MinimumTokenBytes = 32;
    internal const int MaximumTokenBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IOptionsMonitor<SettlementServiceOptions> _options;

    public SettlementServiceTokenHandler(IOptionsMonitor<SettlementServiceOptions> options)
    {
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(_options.CurrentValue, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(
        SettlementServiceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ApiTokenFile))
            return await ReadTokenFileAsync(options.ApiTokenFile, cancellationToken);

        var token = options.ApiToken;
        if (string.IsNullOrWhiteSpace(token)
            || token.Length is < MinimumTokenBytes or > MaximumTokenBytes
            || token.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                "Services:Settlement SERVICE credential is not configured or is invalid.");
        }

        return token;
    }

    private static async Task<string> ReadTokenFileAsync(
        string tokenFile,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException(
                $"{SettlementServiceOptions.ApiTokenFileKey} must be an absolute mounted-secret path.");

        var info = new FileInfo(tokenFile);
        if (!info.Exists || info.Length is < 1 or > MaximumTokenBytes + 2)
            throw new InvalidOperationException(
                "Settlement SERVICE credential file is missing or outside the allowed size.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(tokenFile, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Settlement SERVICE credential file could not be read.", ex);
        }

        try
        {
            var start = 0;
            var end = bytes.Length;
            while (start < end && IsAsciiWhitespace(bytes[start])) start++;
            while (end > start && IsAsciiWhitespace(bytes[end - 1])) end--;
            var length = end - start;
            if (length is < MinimumTokenBytes or > MaximumTokenBytes)
                throw new InvalidOperationException(
                    "Settlement SERVICE credential file contains an invalid credential.");

            var token = StrictUtf8.GetString(bytes, start, length);
            if (token.Any(char.IsWhiteSpace))
                throw new InvalidOperationException(
                    "Settlement SERVICE credential file contains an invalid credential.");
            return token;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "Settlement SERVICE credential file contains an invalid credential.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
