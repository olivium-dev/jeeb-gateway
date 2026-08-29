using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Notifications;

/// <summary>
/// Authenticates gateway-owned registration and recovery calls to the push
/// relay. The durable notification owner uses the same environment secret.
/// </summary>
public sealed class PushRelayCredentialHandler(IConfiguration configuration)
    : DelegatingHandler
{
    internal const string HeaderName = "X-Api-Key";
    internal const string TokenFileKey = "PushNotificationServiceApi:InternalApiKeyFile";
    internal const string TokenKey = "PushNotificationServiceApi:InternalApiKey";
    internal const int MaximumTokenBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(configuration, cancellationToken);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, token);
        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var path = configuration[TokenFileKey];
        var direct = configuration[TokenKey];
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(direct))
            throw new InvalidOperationException(
                $"Configure only one of {TokenFileKey} and {TokenKey}.");

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException(
                    TokenFileKey + " must be an absolute mounted-secret path.");
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Push relay credential file could not be read.", ex);
            }

            try
            {
                return Decode(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        if (string.IsNullOrWhiteSpace(direct)
            || direct.Length > MaximumTokenBytes
            || direct.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"No valid push relay credential is configured in {TokenFileKey} or {TokenKey}.");
        }
        return direct;
    }

    private static string Decode(byte[] bytes)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && IsAsciiWhitespace(bytes[start])) start++;
        while (end > start && IsAsciiWhitespace(bytes[end - 1])) end--;
        var length = end - start;
        if (length is < 1 or > MaximumTokenBytes)
            throw new InvalidOperationException("Push relay credential file is invalid.");
        try
        {
            var token = StrictUtf8.GetString(bytes, start, length);
            if (token.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Push relay credential file is invalid.");
            return token;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Push relay credential file is invalid.", ex);
        }
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

public sealed class PushRelayCredentialHealthCheck(IConfiguration configuration)
    : IHealthCheck
{
    internal const string Name = "push-relay-credential";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await PushRelayCredentialHandler.ReadTokenAsync(
                configuration, cancellationToken);
            return HealthCheckResult.Healthy("push relay credential resolves");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
