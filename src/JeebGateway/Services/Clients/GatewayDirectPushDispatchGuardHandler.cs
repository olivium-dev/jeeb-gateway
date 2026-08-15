using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Prevents the gateway from acting as a second push producer after durable
/// notification dispatch moves to notification-service.
/// </summary>
public sealed class GatewayDirectPushDispatchOptions
{
    public const string SectionName = "PushNotificationServiceApi:GatewayDirectDispatch";

    /// <summary>
    /// Emergency rollback switch. The committed and missing-value default is
    /// false so a new environment fails closed.
    /// </summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Blocks only push send operations on the generated push client. Device-token
/// registration/deletion, health, and idempotency recovery remain available.
/// </summary>
public sealed class GatewayDirectPushDispatchGuardHandler : DelegatingHandler
{
    internal const string DisabledProblemCode = "gateway_direct_push_dispatch_disabled";

    private readonly IOptions<GatewayDirectPushDispatchOptions> _options;

    public GatewayDirectPushDispatchGuardHandler(
        IOptions<GatewayDirectPushDispatchOptions> options)
    {
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled && IsDirectDispatch(request))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new
                {
                    code = DisabledProblemCode,
                    detail = "Gateway direct push dispatch is disabled; notification-service is the sole push producer.",
                }),
            });
        }

        return base.SendAsync(request, cancellationToken);
    }

    internal static bool IsDirectDispatch(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is null)
        {
            return false;
        }

        var path = request.RequestUri.IsAbsoluteUri
            ? request.RequestUri.AbsolutePath
            : request.RequestUri.OriginalString.Split('?', '#')[0];
        path = "/" + path.TrimStart('/');

        return path.StartsWith("/api/v1/sent-payload/device/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/sent-payload/user/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/sent-payload/broadcast", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/sent-payload/broadcast/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/sent-payload/topic/", StringComparison.OrdinalIgnoreCase);
    }
}
