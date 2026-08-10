using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.StateService;

/// <summary>
/// Attaches the dedicated, file-backed jeeb-state-service credential to every
/// owner call. The file is read for each request so an atomic secret-file swap
/// rotates credentials without restarting the gateway. Token material is never
/// cached, persisted by the gateway, or written to logs.
/// </summary>
public sealed class StateServiceCredentialHandler(StateServiceOptions options) : DelegatingHandler
{
    internal const int MinimumTokenBytes = 32;
    internal const int MaximumTokenBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(options.ServiceTokenFile, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(string? tokenFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenFile) || !Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException(
                "JeebStateService:ServiceTokenFile must be an absolute mounted-secret path.");

        var info = new FileInfo(tokenFile);
        if (!info.Exists || info.Length is < 1 or > MaximumTokenBytes + 2)
            throw new InvalidOperationException(
                "JeebStateService service-token file is missing or outside the allowed size.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(tokenFile, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "JeebStateService service-token file could not be read.", ex);
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
                    "JeebStateService service-token file contains an invalid credential.");

            var token = StrictUtf8.GetString(bytes, start, length);
            if (token.Any(char.IsWhiteSpace))
                throw new InvalidOperationException(
                    "JeebStateService service-token file contains an invalid credential.");
            return token;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "JeebStateService service-token file contains an invalid credential.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
