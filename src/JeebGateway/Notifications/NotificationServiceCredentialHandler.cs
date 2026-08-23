using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Notifications;

/// <summary>
/// Adds the notification owner credential without caching or logging it. A
/// mounted file is preferred for rotation; the environment-backed configuration
/// values (ServiceToken, NOTIFICATION_SERVICE_TOKEN, ApiToken) are the
/// supported non-container fallbacks. A configured-but-absent file falls
/// through to the value-backed keys with a warning instead of failing closed.
/// </summary>
public sealed class NotificationServiceCredentialHandler(
    IConfiguration configuration,
    ILogger<NotificationServiceCredentialHandler> logger)
    : DelegatingHandler
{
    internal const string HeaderName = "X-Notification-Service-Token";
    internal const int MaximumTokenBytes = 4096;

    internal const string ServiceTokenFileKey = "ServiceNotificationClient:ServiceTokenFile";
    internal const string ServiceTokenKey = "ServiceNotificationClient:ServiceToken";
    internal const string EnvironmentTokenKey = "NOTIFICATION_SERVICE_TOKEN";
    internal const string ApiTokenKey = "ServiceNotificationClient:ApiToken";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(configuration, logger, cancellationToken);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, token);
        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        var file = configuration[ServiceTokenFileKey];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!Path.IsPathFullyQualified(file))
                throw new InvalidOperationException(
                    ServiceTokenFileKey + " must be an absolute mounted-secret path.");

            var info = new FileInfo(file);
            if (info.Exists)
            {
                if (info.Length is < 1 or > MaximumTokenBytes + 2)
                    throw new InvalidOperationException(
                        "Notification service-token file is outside the allowed size.");

                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(file, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        "Notification service-token file could not be read.", ex);
                }

                try
                {
                    return Decode(bytes, "Notification service-token file contains an invalid credential.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            // 608debf regression: a missing mounted secret must not out-rank an
            // available env token; warn loudly and fall through to the value keys.
            logger.LogWarning(
                "event={event} key={key} path={path} detail={detail}",
                "notif.credential.token_file_missing", ServiceTokenFileKey, file,
                "configured token file does not exist; falling back to value-backed configuration");
        }

        // IConfiguration maps the double-underscore env forms of every key below
        // without exposing any value to logs or exception text.
        var (value, sourceKey) = FirstConfiguredValue(configuration);
        if (value is null)
        {
            var fileDetail = string.IsNullOrWhiteSpace(file)
                ? ServiceTokenFileKey + " (not set)"
                : ServiceTokenFileKey + $" (file missing at '{file}')";
            throw new InvalidOperationException(
                "No notification service credential is configured. Tried " + fileDetail
                + $", {ServiceTokenKey}, {EnvironmentTokenKey} and {ApiTokenKey}.");
        }

        if (value.Length > MaximumTokenBytes || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Notification service token from {sourceKey} is invalid.");
        }
        return value;
    }

    private static (string? Value, string Key) FirstConfiguredValue(IConfiguration configuration)
    {
        foreach (var key in new[] { ServiceTokenKey, EnvironmentTokenKey, ApiTokenKey })
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value)) return (value, key);
        }
        return (null, "");
    }

    private static string Decode(byte[] bytes, string error)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && IsAsciiWhitespace(bytes[start])) start++;
        while (end > start && IsAsciiWhitespace(bytes[end - 1])) end--;
        var length = end - start;
        if (length is < 1 or > MaximumTokenBytes)
            throw new InvalidOperationException(error);
        try
        {
            var token = StrictUtf8.GetString(bytes, start, length);
            if (token.Any(char.IsWhiteSpace)) throw new InvalidOperationException(error);
            return token;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(error, ex);
        }
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
