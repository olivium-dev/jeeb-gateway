namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real notification-service (FastAPI, Mongo
/// <c>jeeb_notifications</c>). Hand-coded against the verified routes on
/// notification-service/main.py — <c>GET /notifications</c> (paginated list,
/// line 753) — pending an NSwag spec we can generate from (the committed
/// contract at <c>contracts/notification-service.openapi.json</c> is still a
/// placeholder with empty <c>paths</c>).
///
/// The named "notification" HttpClient registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> supplies
/// BaseAddress (<c>Services:Notification:BaseUrl</c>) + the org-standard
/// resilience pipeline, so this class never thinks about retry/timeout/breaker.
///
/// NOTE: the upstream service exposes NO preferences endpoint — notification
/// toggles are gateway-local state held by
/// <see cref="JeebGateway.NotificationPreferences.INotificationPreferencesStore"/>.
/// This client therefore covers the READ/list surface that actually reaches the
/// notification DB; <see cref="GetPreferencesAsync"/> is a documented adapter
/// that maps to the closest real read (the user's notifications by receiver) so
/// the BFF has a single seam to grow into once preferences land upstream.
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface INotificationServiceClient
{
    /// <summary>
    /// Real DB read: proxies <c>GET /notifications</c> on notification-service,
    /// which queries Mongo <c>jeeb_notifications</c> with the supplied filters
    /// and pagination. This is the tested read path that reaches the
    /// notification-service database.
    /// </summary>
    Task<NotificationListResponse> ListNotificationsAsync(
        NotificationListQuery query,
        CancellationToken ct);

    /// <summary>
    /// Convenience read of a single user's notifications by receiver id, built
    /// on top of <see cref="ListNotificationsAsync"/>. Used as the "preferences
    /// get" seam: until the upstream service exposes a preferences document, the
    /// closest real read is the receiver's notification list, which still hits
    /// the notification DB.
    /// </summary>
    Task<NotificationListResponse> GetByReceiverAsync(
        string receiverId,
        int page,
        int pageSize,
        CancellationToken ct);
}

/// <summary>
/// Filter + pagination inputs for <see cref="INotificationServiceClient.ListNotificationsAsync"/>.
/// Field names mirror the <c>GET /notifications</c> query parameters on
/// notification-service/main.py (page, page_size, status, notification_type,
/// sender, receiver).
/// </summary>
public sealed class NotificationListQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 10;
    public string? Status { get; init; }
    public string? NotificationType { get; init; }
    public string? Sender { get; init; }
    public string? Receiver { get; init; }
}

/// <summary>
/// Shape of the <c>GET /notifications</c> response body on notification-service.
/// Only the pagination envelope + items are surfaced; per-notification documents
/// are kept as a loosely-typed dictionary because the upstream schema is dynamic
/// (notification_config.json drives per-type payloads) and the BFF list view does
/// not need to bind every field.
/// </summary>
public sealed class NotificationListResponse
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalNotifications { get; init; }
    public int TotalPages { get; init; }
    public bool HasNext { get; init; }
    public bool HasPrevious { get; init; }
    public IReadOnlyList<NotificationListItem> Notifications { get; init; } =
        Array.Empty<NotificationListItem>();
}

/// <summary>
/// A single notification document as returned by notification-service. The
/// upstream payload is dynamic, so the strongly-typed fields cover the stable
/// <c>BaseNotification</c> columns and the rest is preserved verbatim.
/// </summary>
public sealed class NotificationListItem
{
    public string? Id { get; init; }
    public string? NotificationId { get; init; }
    public string? Sender { get; init; }
    public string? Receiver { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? NotificationType { get; init; }
    public bool Deactivated { get; init; }
}
