using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JeebGateway.Notifications;

/// <summary>
/// Stateless client for the notification-service ownership boundary. The owner
/// persists the notification and its dispatch state before acknowledging the
/// command; the gateway deliberately keeps no outbox, retry queue, token store,
/// or delivery tracker of its own.
/// </summary>
public interface INotificationOwnerClient
{
    Task<NotificationOwnerAcceptance> PublishAsync(
        NotificationOwnerEvent notification,
        CancellationToken cancellationToken);

    Task<JsonElement> GetDeadLettersAsync(CancellationToken cancellationToken);
}

public sealed record NotificationOwnerEvent(
    Guid NotificationId,
    string Receiver,
    string Title,
    string Body,
    string EventType,
    IReadOnlyDictionary<string, object?> Data,
    string Locale = "en",
    string Sender = "jeeb-gateway");

public sealed record NotificationOwnerAcceptance(Guid NotificationId);

public sealed class NotificationOwnerConflictException : Exception
{
    public NotificationOwnerConflictException(Guid notificationId)
        : base($"Notification id '{notificationId:D}' is already bound to a different command.")
    {
        NotificationId = notificationId;
    }

    public Guid NotificationId { get; }
}

public sealed class NotificationOwnerClient : INotificationOwnerClient
{
    public const string HttpClientName = "NotificationOwnerClient";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public NotificationOwnerClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<NotificationOwnerAcceptance> PublishAsync(
        NotificationOwnerEvent notification,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "notifications/events")
        {
            Content = JsonContent.Create(new
            {
                notification_id = notification.NotificationId,
                receiver = notification.Receiver,
                title = notification.Title,
                body = notification.Body,
                event_type = notification.EventType,
                data = notification.Data,
                sender = notification.Sender,
                locale = notification.Locale,
            }, options: Json),
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            notification.NotificationId.ToString("D"));

        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new NotificationOwnerConflictException(notification.NotificationId);
        }

        response.EnsureSuccessStatusCode();
        return new NotificationOwnerAcceptance(notification.NotificationId);
    }

    public async Task<JsonElement> GetDeadLettersAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "dlq");
        var adminToken = _configuration["ServiceNotificationClient:DlqAdminToken"];
        if (!string.IsNullOrWhiteSpace(adminToken))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        }

        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
    }
}

/// <summary>
/// Maps an arbitrary caller idempotency key onto the UUIDv4-only owner
/// contract. Replays of a supplied key resolve to the same UUID; calls without
/// a key intentionally receive a fresh identity.
/// </summary>
internal static class NotificationOwnerEventId
{
    public static Guid FromIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Guid.NewGuid();
        }

        var trimmed = idempotencyKey.Trim();
        if (Guid.TryParse(trimmed, out var parsed) && IsVersionFour(parsed))
        {
            return parsed;
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))
            .ToLowerInvariant();
        return Guid.ParseExact(
            $"{hash[..8]}-{hash[8..12]}-4{hash[13..16]}-8{hash[17..20]}-{hash[20..32]}",
            "D");
    }

    private static bool IsVersionFour(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }
}
