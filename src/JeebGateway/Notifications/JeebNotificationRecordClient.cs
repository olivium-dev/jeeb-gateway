using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Notifications;

/// <summary>
/// Hand-written typed notification-service client for the two FM-1 durable
/// notification creates and their exact-correlation read-back. The registered
/// pipeline has a breaker and timeout but no retry and no auth handlers.
/// </summary>
public sealed class JeebNotificationRecordClient
{
    public const string HttpClientName = "JeebNotificationRecordClient";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public JeebNotificationRecordClient(HttpClient http) => _http = http;

    public Task<HttpStatusCode> PostOfferReceivedAsync(
        OfferReceivedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(OfferReceivedNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostOfferAcceptedAsync(
        OfferAcceptedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(OfferAcceptedNotificationRecord.TemplateKey, record, cancellationToken);

    public async Task<bool> ContainsCorrelationIdAsync(
        string recipientId,
        string notificationCorrelationId,
        CancellationToken cancellationToken)
    {
        var path =
            $"messages/receiver/{Uri.EscapeDataString(recipientId)}?page=1&page_size=100&read_status=all";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var envelope = await response.Content
            .ReadFromJsonAsync<NotificationMessagesEnvelope>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return envelope?.Messages.Any(row =>
            string.Equals(
                row.NotificationCorrelationId,
                notificationCorrelationId,
                StringComparison.Ordinal)) == true;
    }

    private async Task<HttpStatusCode> PostAsync<TRecord>(
        string templateKey,
        TRecord record,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync($"notifications/{templateKey}", record, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return response.StatusCode;
    }

    private sealed record NotificationMessagesEnvelope
    {
        [JsonPropertyName("messages")]
        public IReadOnlyList<NotificationMessageRow> Messages { get; init; }
            = Array.Empty<NotificationMessageRow>();
    }

    private sealed record NotificationMessageRow
    {
        [JsonPropertyName("notification_id")]
        public string? NotificationCorrelationId { get; init; }
    }
}
