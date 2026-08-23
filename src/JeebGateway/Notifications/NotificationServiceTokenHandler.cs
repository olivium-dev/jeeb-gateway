using Microsoft.Extensions.Configuration;

namespace JeebGateway.Notifications;

/// <summary>
/// Sends the notification-service owner credential on every outbound call.
/// Credential loading is shared with <see cref="NotificationServiceCredentialHandler"/>:
/// the mounted file is re-read for each request so rotation does not require a restart;
/// a configured-but-absent file falls back to the value-backed keys, and only a fully
/// unconfigured credential fails closed instead of silently issuing an anonymous call.
/// </summary>
public sealed class NotificationServiceTokenHandler : DelegatingHandler
{
    public const string HeaderName = "X-Notification-Service-Token";

    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationServiceTokenHandler> _logger;

    public NotificationServiceTokenHandler(
        IConfiguration configuration,
        ILogger<NotificationServiceTokenHandler> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await NotificationServiceCredentialHandler.ReadTokenAsync(
            _configuration,
            _logger,
            cancellationToken);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, token);

        return await base.SendAsync(request, cancellationToken);
    }
}
