using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Adds delivery-service's route-wide service credential to every owner call.
/// Production reads the credential from an absolute mounted-secret path on
/// every request, allowing rotation without restarting or caching secret data.
/// </summary>
public sealed class DeliveryServiceCredentialHandler(
    IConfiguration configuration,
    IHostEnvironment environment) : DelegatingHandler
{
    internal const string HeaderName = "X-Delivery-Service-Token";
    internal const int MinimumTokenBytes = 32;
    internal const int MaximumTokenBytes = 4096;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(configuration, environment, cancellationToken);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, token);
        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var tokenFile = configuration["DELIVERY_SERVICE_TOKEN_FILE"]
                        ?? configuration["Services:Delivery:ServiceTokenFile"];
        if (!string.IsNullOrWhiteSpace(tokenFile))
            return await ReadFileAsync(tokenFile, cancellationToken);

        // The deployed gateway must only consume a mounted secret. A direct
        // value is deliberately limited to local development and explicit test
        // hosts so production cannot drift back to an environment-held secret.
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException(
                "DELIVERY_SERVICE_TOKEN_FILE must name an absolute mounted-secret path.");

        var token = configuration["DELIVERY_SERVICE_TOKEN"]
                    ?? configuration["Services:Delivery:ServiceToken"];
        if (!IsValidToken(token))
        {
            throw new InvalidOperationException(
                "Delivery service credential is not configured or is invalid.");
        }

        return token!;
    }

    private static async Task<string> ReadFileAsync(
        string tokenFile,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException(
                "DELIVERY_SERVICE_TOKEN_FILE must name an absolute mounted-secret path.");

        var info = new FileInfo(tokenFile);
        if (!info.Exists || info.Length is < 1 or > MaximumTokenBytes + 2)
            throw new InvalidOperationException(
                "Delivery service-token file is missing or outside the allowed size.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(tokenFile, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Delivery service-token file could not be read.", ex);
        }

        try
        {
            return DecodeMountedToken(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Delivery-service parses one header value, so tokens are deliberately
    /// restricted to header-safe visible ASCII. Comma is excluded because HTTP
    /// header folding can turn duplicate values into one comma-separated value.
    /// </summary>
    internal static bool IsValidToken(string? token)
    {
        if (token is null || token.Length is < MinimumTokenBytes or > MaximumTokenBytes)
            return false;
        return token.All(value => value is >= '!' and <= '~' && value != ',');
    }

    internal static string DecodeMountedToken(ReadOnlySpan<byte> bytes)
    {
        // Secret-file writers normally append exactly one LF or CRLF. Trim only
        // that terminal record delimiter; any other leading/trailing whitespace,
        // repeated newline, control byte, non-ASCII byte, or comma is invalid.
        var length = bytes.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\n')
        {
            length--;
            if (length > 0 && bytes[length - 1] == (byte)'\r')
                length--;
        }

        if (length is < MinimumTokenBytes or > MaximumTokenBytes)
            throw InvalidFileCredential();
        for (var index = 0; index < length; index++)
        {
            var value = bytes[index];
            if (value is < (byte)'!' or > (byte)'~' || value == (byte)',')
                throw InvalidFileCredential();
        }

        return Encoding.ASCII.GetString(bytes[..length]);
    }

    internal static bool TryValidateMountedTokenFile(string path, out string? error)
    {
        error = null;
        byte[]? bytes = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > MaximumTokenBytes + 2)
            {
                error = "file is missing or outside the allowed size";
                return false;
            }
            bytes = File.ReadAllBytes(path);
            _ = DecodeMountedToken(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException)
        {
            error = ex is InvalidOperationException
                ? "credential is not 32-4096 bytes of comma-free visible ASCII with at most one terminal newline"
                : "file could not be read";
            return false;
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static InvalidOperationException InvalidFileCredential() =>
        new("Delivery service-token file contains an invalid credential.");
}
