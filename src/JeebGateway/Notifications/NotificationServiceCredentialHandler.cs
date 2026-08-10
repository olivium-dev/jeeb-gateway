using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Notifications;

/// <summary>
/// Adds the notification owner credential without caching or logging it. A
/// mounted file is preferred for rotation; the environment-backed configuration
/// value is the supported non-container fallback.
/// </summary>
public sealed class NotificationServiceCredentialHandler(IConfiguration configuration)
    : DelegatingHandler
{
    internal const string HeaderName = "X-Notification-Service-Token";
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
        CancellationToken ct)
    {
        var file = configuration["ServiceNotificationClient:ServiceTokenFile"];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!Path.IsPathFullyQualified(file))
                throw new InvalidOperationException(
                    "ServiceNotificationClient:ServiceTokenFile must be an absolute mounted-secret path.");

            var info = new FileInfo(file);
            if (!info.Exists || info.Length is < 1 or > MaximumTokenBytes + 2)
                throw new InvalidOperationException(
                    "Notification service-token file is missing or outside the allowed size.");

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

        // IConfiguration maps ServiceNotificationClient__ServiceToken and the
        // explicit NOTIFICATION_SERVICE_TOKEN variable without exposing either
        // value to logs or exception text.
        var value = configuration["ServiceNotificationClient:ServiceToken"]
                    ?? configuration["NOTIFICATION_SERVICE_TOKEN"];
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumTokenBytes
            || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                "Notification owner service token is not configured or is invalid.");
        }
        return value;
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
