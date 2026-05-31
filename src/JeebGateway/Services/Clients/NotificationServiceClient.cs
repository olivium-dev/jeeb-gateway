using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="INotificationServiceClient"/>.
/// Hand-coded against the published routes on notification-service/main.py
/// (<c>GET /notifications</c>, line 753) pending an NSwag spec — the committed
/// <c>contracts/notification-service.openapi.json</c> is still a placeholder.
///
/// The named "notification" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress + the org-standard resilience pipeline, so this class never has
/// to think about retry/timeout/circuit-breaker.
///
/// The upstream returns snake_case JSON (page_size, notification_id,
/// total_notifications, ...), so this client uses an explicit
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/> policy plus per-field
/// <see cref="JsonPropertyNameAttribute"/> on the wire DTOs.
/// </summary>
public sealed class NotificationServiceClient : INotificationServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public NotificationServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<NotificationListResponse> ListNotificationsAsync(
        NotificationListQuery query,
        CancellationToken ct)
    {
        // GET /notifications?page=..&page_size=..&receiver=..&status=..&notification_type=..&sender=..
        // Hits notification-service which queries Mongo jeeb_notifications.
        var parameters = new List<string>
        {
            $"page={query.Page}",
            $"page_size={query.PageSize}",
        };

        if (!string.IsNullOrWhiteSpace(query.Receiver))
            parameters.Add($"receiver={Uri.EscapeDataString(query.Receiver)}");
        if (!string.IsNullOrWhiteSpace(query.Status))
            parameters.Add($"status={Uri.EscapeDataString(query.Status)}");
        if (!string.IsNullOrWhiteSpace(query.NotificationType))
            parameters.Add($"notification_type={Uri.EscapeDataString(query.NotificationType)}");
        if (!string.IsNullOrWhiteSpace(query.Sender))
            parameters.Add($"sender={Uri.EscapeDataString(query.Sender)}");

        var url = "notifications?" + string.Join("&", parameters);

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync(response, ct);
    }

    public Task<NotificationListResponse> GetByReceiverAsync(
        string receiverId,
        int page,
        int pageSize,
        CancellationToken ct)
        => ListNotificationsAsync(
            new NotificationListQuery
            {
                Receiver = receiverId,
                Page = page,
                PageSize = pageSize,
            },
            ct);

    private static async Task<NotificationListResponse> DeserializeAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var wire = await response.Content.ReadFromJsonAsync<WireListResponse>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new NotificationListResponse
        {
            Page = wire.Page,
            PageSize = wire.PageSize,
            TotalNotifications = wire.TotalNotifications,
            TotalPages = wire.TotalPages,
            HasNext = wire.HasNext,
            HasPrevious = wire.HasPrevious,
            Notifications = (wire.Notifications ?? new List<WireNotification>())
                .Select(static n => new NotificationListItem
                {
                    Id = n.Id,
                    NotificationId = n.NotificationId,
                    Sender = n.Sender,
                    Receiver = n.Receiver,
                    Title = n.Title,
                    Subtitle = n.Subtitle,
                    Description = n.Description,
                    Status = n.Status,
                    NotificationType = n.NotificationType,
                    Deactivated = n.Deactivated,
                })
                .ToList(),
        };
    }

    // --- wire DTOs (snake_case as returned by notification-service/main.py) ---

    private sealed class WireListResponse
    {
        [JsonPropertyName("page")] public int Page { get; init; }
        [JsonPropertyName("page_size")] public int PageSize { get; init; }
        [JsonPropertyName("total_notifications")] public int TotalNotifications { get; init; }
        [JsonPropertyName("total_pages")] public int TotalPages { get; init; }
        [JsonPropertyName("has_next")] public bool HasNext { get; init; }
        [JsonPropertyName("has_previous")] public bool HasPrevious { get; init; }
        [JsonPropertyName("notifications")] public List<WireNotification>? Notifications { get; init; }
    }

    private sealed class WireNotification
    {
        [JsonPropertyName("_id")] public string? Id { get; init; }
        [JsonPropertyName("notification_id")] public string? NotificationId { get; init; }
        [JsonPropertyName("sender")] public string? Sender { get; init; }
        [JsonPropertyName("receiver")] public string? Receiver { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("subtitle")] public string? Subtitle { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("notification_type")] public string? NotificationType { get; init; }
        [JsonPropertyName("deactivated")] public bool Deactivated { get; init; }
    }
}
