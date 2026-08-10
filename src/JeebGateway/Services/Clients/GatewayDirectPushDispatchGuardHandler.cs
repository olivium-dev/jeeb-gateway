using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Notifications;
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
    private readonly INotificationOwnerClient? _notificationOwner;

    public GatewayDirectPushDispatchGuardHandler(
        IOptions<GatewayDirectPushDispatchOptions> options,
        INotificationOwnerClient? notificationOwner = null)
    {
        _options = options;
        _notificationOwner = notificationOwner;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled && IsDirectDispatch(request))
        {
            if (_notificationOwner is not null && IsPerUserDispatch(request))
            {
                return await RoutePerUserDispatchToOwnerAsync(request, cancellationToken);
            }

            return DisabledResponse(request);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> RoutePerUserDispatchToOwnerAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = NormalizedPath(request);
            var receiver = Uri.UnescapeDataString(
                path["/api/v1/sent-payload/user/".Length..]);
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!body.RootElement.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return DisabledResponse(request);
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                           payload.GetRawText())
                       ?? new Dictionary<string, object?>();
            var title = StringValue(payload, "title") ?? "Notification";
            var message = StringValue(payload, "body") ?? string.Empty;
            var discriminator = StringValue(payload, "event_type")
                                ?? StringValue(payload, "type")
                                ?? StringValue(payload, "category")
                                ?? "generic";
            var callerKey = StringValue(payload, "notification_id")
                            ?? StringValue(payload, "notificationId")
                            ?? StringValue(payload, "idempotency_key");
            var notificationId = NotificationOwnerEventId
                .FromIdempotencyKey(callerKey);

            await _notificationOwner!.PublishAsync(
                new NotificationOwnerEvent(
                    notificationId,
                    receiver,
                    title,
                    message,
                    $"gateway.{discriminator}",
                    data,
                    StringValue(payload, "language") ?? "en"),
                cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new
                {
                    message = "Durably accepted by notification-service; delivery is owner-managed.",
                    timestamp = DateTimeOffset.UtcNow,
                    owner_notification_id = notificationId,
                }),
            };
        }
        catch (NotificationOwnerConflictException)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new
                {
                    code = "notification_idempotency_conflict",
                    detail = "The notification identity belongs to a different command.",
                }),
            };
        }
        catch (Exception)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new
                {
                    code = "notification_owner_unavailable",
                    detail = "Notification-service did not durably accept the command.",
                }),
            };
        }
    }

    private static HttpResponseMessage DisabledResponse(HttpRequestMessage request)
    {
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = JsonContent.Create(new
            {
                code = DisabledProblemCode,
                detail = "Gateway direct push dispatch is disabled; notification-service is the sole push producer.",
            }),
        };
    }

    internal static bool IsDirectDispatch(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is null)
        {
            return false;
        }

        var path = NormalizedPath(request);

        return path.StartsWith("/api/v1/sent-payload/device/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/sent-payload/user/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/sent-payload/broadcast", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/sent-payload/broadcast/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/sent-payload/topic/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPerUserDispatch(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && NormalizedPath(request).StartsWith(
            "/api/v1/sent-payload/user/",
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizedPath(HttpRequestMessage request)
    {
        if (request.RequestUri is null)
        {
            return "/";
        }

        var path = request.RequestUri.IsAbsoluteUri
            ? request.RequestUri.AbsolutePath
            : request.RequestUri.OriginalString.Split('?', '#')[0];
        return "/" + path.TrimStart('/');
    }

    private static string? StringValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
