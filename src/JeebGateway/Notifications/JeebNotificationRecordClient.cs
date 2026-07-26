using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Notifications;

/// <summary>
/// Hand-written typed notification-service client for the durable notification
/// creates and their exact-correlation read-back. The registered pipeline has a
/// breaker and timeout but no retry and no auth handlers.
///
/// <para>b02 step 6a added the six previously-unwritten types. Every method here is a
/// one-liner over <see cref="PostAsync{TRecord}"/> on purpose: this class is transport
/// only, so a new type costs one line and cannot introduce a new failure mode. All
/// classification, budgeting and the silent gate live in
/// <see cref="NotificationRecordWriter"/>, which is the SOLE caller — do not add a
/// second caller, or the silent policy stops being enforceable at one choke point.</para>
///
/// <para>All eight paths were probed live against the centre on 2026-07-26: each answers
/// 422 for an empty body, i.e. the route exists. The ninth catalog type that used to
/// exist, <c>jeeb.offer_rejected</c>, answered 405 and was retired in step 6b — there is
/// deliberately no method for it, and adding one would fail on every call.</para>
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

    // ── b02 step 6a — the six types that had no writer ───────────────────────────────

    /// <summary>
    /// <c>POST notifications/jeeb.delivery_status_updated</c>. Reachable, but see
    /// <see cref="DeliveryStatusUpdatedNotificationRecord"/>: owner ruling D4 classifies this
    /// type SILENT, so <see cref="NotificationRecordWriter"/> never actually calls this method
    /// today. It exists so the gate is exercised at a real writer, not a stand-in.
    /// </summary>
    public Task<HttpStatusCode> PostDeliveryStatusUpdatedAsync(
        DeliveryStatusUpdatedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(DeliveryStatusUpdatedNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostSettlementPaidAsync(
        SettlementPaidNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(SettlementPaidNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostKycApprovedAsync(
        KycApprovedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(KycApprovedNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostKycRejectedAsync(
        KycRejectedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(KycRejectedNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostDisputeResolvedAsync(
        DisputeResolvedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(DisputeResolvedNotificationRecord.TemplateKey, record, cancellationToken);

    public Task<HttpStatusCode> PostRatingAutoRevealedAsync(
        RatingAutoRevealedNotificationRecord record,
        CancellationToken cancellationToken)
        => PostAsync(RatingAutoRevealedNotificationRecord.TemplateKey, record, cancellationToken);

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
