using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Notifications;

/// <summary>
/// Authenticates gateway-owned registration and recovery calls to the push
/// relay with a credential distinct from notification delivery.
/// </summary>
public sealed class PushRelayCredentialHandler(IConfiguration configuration)
    : DelegatingHandler
{
    internal const string HeaderName = "X-Api-Key";
    internal const string CallerHeaderName = "X-Caller-Id";
    internal const string CallerId = "jeeb-gateway";
    internal const string TokenFileKey = "PushNotificationServiceApi:GatewayApiKeyFile";
    internal const string TokenKey = "PushNotificationServiceApi:GatewayApiKey";
    internal const int MaximumTokenBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(configuration, cancellationToken);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, token);
        request.Headers.Remove(CallerHeaderName);
        request.Headers.TryAddWithoutValidation(CallerHeaderName, CallerId);
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

/// <summary>
/// Verifies the provider-side, key-derived gateway registration scope without
/// mutating provider state. Resolving a local secret alone is insufficient:
/// readiness must prove the mounted credential is accepted by the relay.
/// </summary>
public sealed class PushRelayCredentialHealthCheck(IHttpClientFactory httpClientFactory)
    : IHealthCheck
{
    internal const string Name = "push-relay-scoped-readiness";
    internal const string ReadinessPath = "/api/v1/register/ready";
    private const string ExpectedStatus = "ready";
    private const string ExpectedScope = "gateway.registration";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReadinessPath);
            using var response = await httpClientFactory
                .CreateClient("ServicePushNotificationClient")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode != System.Net.HttpStatusCode.OK
                || response.Content is null)
            {
                return HealthCheckResult.Unhealthy("push relay scoped readiness check failed");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() != ExpectedStatus
                || !root.TryGetProperty("scope", out var scope)
                || scope.ValueKind != JsonValueKind.String
                || scope.GetString() != ExpectedScope)
            {
                return HealthCheckResult.Unhealthy("push relay scoped readiness check failed");
            }

            return HealthCheckResult.Healthy("push relay scoped readiness check passed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("push relay scoped readiness check failed");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                  or InvalidOperationException
                                  or IOException
                                  or JsonException)
        {
            return HealthCheckResult.Unhealthy("push relay scoped readiness check failed");
        }
    }
}
