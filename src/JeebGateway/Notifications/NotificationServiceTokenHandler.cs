using Microsoft.Extensions.Configuration;

namespace JeebGateway.Notifications;

/// <summary>
/// Sends the notification-service service token (<c>X-Notification-Service-Token</c>)
/// on every outbound call when <c>ServiceNotificationClient:ApiToken</c> is configured.
/// Inert (adds nothing) while the key is unset, so this handler can ship ahead of the
/// notification-service auth cutover and the same binary works on both sides of it.
/// </summary>
public sealed class NotificationServiceTokenHandler : DelegatingHandler
{
    public const string HeaderName = "X-Notification-Service-Token";
    public const string ConfigKey = "ServiceNotificationClient:ApiToken";

    private readonly string? _token;

    public NotificationServiceTokenHandler(IConfiguration configuration)
        => _token = configuration[ConfigKey];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) && !request.Headers.Contains(HeaderName))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, _token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
