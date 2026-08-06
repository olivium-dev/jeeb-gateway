using System.Net;
using System.Text.Json;
using JeebGateway.Conversations.Client;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Cases;

public sealed class CaseEvidenceOptions
{
    public const string SectionName = "Cases:Evidence";
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxChatMessages { get; set; } = 2000;
    public int MaxGpsPoints { get; set; } = 2000;
    public int MaxPagesPerSource { get; set; } = 20;
}

public interface ICaseEvidenceCollector
{
    Task<IReadOnlyList<GenericCaseEvidenceV1>> CaptureAsync(string deliveryId,
        string viewerUserId, IReadOnlyList<string> attachmentRefs, CancellationToken ct);
}

/// <summary>Stateless, viewer-scoped evidence composition with per-source deadlines.</summary>
public sealed class CaseEvidenceCollector : ICaseEvidenceCollector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IJeebConversationClient _chat;
    private readonly ICaseDeliveryClient _delivery;
    private readonly IGeoHistoryClient _geo;
    private readonly IOptionsMonitor<CaseEvidenceOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CaseEvidenceCollector> _log;

    public CaseEvidenceCollector(IJeebConversationClient chat, ICaseDeliveryClient delivery,
        IGeoHistoryClient geo, IOptionsMonitor<CaseEvidenceOptions> options,
        TimeProvider clock, ILogger<CaseEvidenceCollector> log)
    {
        _chat = chat; _delivery = delivery; _geo = geo; _options = options; _clock = clock; _log = log;
    }

    public async Task<IReadOnlyList<GenericCaseEvidenceV1>> CaptureAsync(string deliveryId,
        string viewerUserId, IReadOnlyList<string> attachmentRefs, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var chat = CaptureChatAsync(deliveryId, viewerUserId, options, ct);
        var history = CaptureDeliveryHistoryAsync(deliveryId, options.SourceTimeout, ct);
        var gps = CaptureGpsAsync(deliveryId, options, ct);
        await Task.WhenAll(chat, history, gps);
        var result = new List<GenericCaseEvidenceV1> { await chat, await history, await gps };
        if (attachmentRefs.Count > 0)
        {
            result.Add(Evidence("cdn_attachments", "complete", attachmentRefs.Count, null,
                JsonSerializer.SerializeToElement(new { objectRefs = attachmentRefs }, Json)));
        }
        return result;
    }

    private async Task<GenericCaseEvidenceV1> CaptureChatAsync(string deliveryId,
        string viewerUserId, CaseEvidenceOptions options, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.SourceTimeout);
        var messages = new List<JeebMessageResponse>();
        string? conversationId = null;
        string? cursor = null;
        DateTimeOffset? asOf = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        try
        {
            var conversation = await _chat.GetConversationByCorrelationAsync(deliveryId, budget.Token);
            conversationId = conversation.ConversationId;
            var max = Math.Clamp(options.MaxChatMessages, 1, 10_000);
            var maxPages = Math.Clamp(options.MaxPagesPerSource, 1, 100);
            while (messages.Count < max)
            {
                budget.Token.ThrowIfCancellationRequested();
                var page = await _chat.ExportMessagesForViewerAsync(conversation.ConversationId,
                    viewerUserId, asOf, cursor, Math.Min(500, max - messages.Count), budget.Token);
                pages++;
                asOf ??= page.AsOf;
                messages.AddRange(page.Messages);
                if (!page.HasMore)
                    return ChatEvidence("complete", null, conversationId, viewerUserId, asOf, messages);
                if (string.IsNullOrWhiteSpace(page.NextCursor))
                    return ChatEvidence("partial", "missing_next_cursor", conversationId, viewerUserId, asOf, messages);
                if (page.Messages.Count == 0)
                    return ChatEvidence("partial", "non_advancing_page", conversationId, viewerUserId, asOf, messages);
                if (!seenCursors.Add(page.NextCursor))
                    return ChatEvidence("partial", "non_advancing_cursor", conversationId, viewerUserId, asOf, messages);
                if (pages >= maxPages)
                    return ChatEvidence("partial", "truncated_max_pages", conversationId, viewerUserId, asOf, messages);
                cursor = page.NextCursor;
            }
            return ChatEvidence("partial", "truncated_max_messages", conversationId, viewerUserId, asOf, messages);
        }
        catch (JeebConversationApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        { return Partial("chat_snapshot", "conversation_not_found"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ChatEvidence("partial", "time_budget_exhausted", conversationId, viewerUserId, asOf, messages); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "event=case.evidence_partial source=chat_snapshot delivery_id={DeliveryId}", deliveryId);
            return messages.Count == 0 ? Partial("chat_snapshot", "upstream_unavailable")
                : ChatEvidence("partial", "upstream_unavailable", conversationId, viewerUserId, asOf, messages);
        }
    }

    private GenericCaseEvidenceV1 ChatEvidence(string status, string? marker, string? conversationId,
        string viewerUserId, DateTimeOffset? asOf, IReadOnlyList<JeebMessageResponse> messages) =>
        Evidence("chat_snapshot", status, messages.Count, marker,
            JsonSerializer.SerializeToElement(new { conversationId, viewerUserId, asOf, messages }, Json));

    private async Task<GenericCaseEvidenceV1> CaptureDeliveryHistoryAsync(string deliveryId,
        TimeSpan timeout, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            var context = await _delivery.GetDeliveryCaseContextAsync(deliveryId, budget.Token);
            if (context is null) return Partial("delivery_history", "delivery_not_found");
            return Evidence("delivery_history", "complete", context.StatusHistory.Count, null,
                JsonSerializer.SerializeToElement(new
                {
                    context.DeliveryId, context.CurrentStatus, context.PartyIds,
                    statusHistory = context.StatusHistory,
                }, Json));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Partial("delivery_history", "time_budget_exhausted"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "event=case.evidence_partial source=delivery_history delivery_id={DeliveryId}", deliveryId);
            return Partial("delivery_history", "upstream_unavailable");
        }
    }

    private async Task<GenericCaseEvidenceV1> CaptureGpsAsync(string deliveryId,
        CaseEvidenceOptions options, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.SourceTimeout);
        var pings = new List<GpsTrackHistoryPoint>();
        string? cursor = null;
        var retentionDays = 30;
        DateTimeOffset? retainedFrom = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        try
        {
            var max = Math.Clamp(options.MaxGpsPoints, 1, 10_000);
            var maxPages = Math.Clamp(options.MaxPagesPerSource, 1, 100);
            while (pings.Count < max)
            {
                budget.Token.ThrowIfCancellationRequested();
                var page = await _geo.GetTrackHistoryPageAsync(
                    deliveryId, cursor, Math.Min(500, max - pings.Count), budget.Token);
                pages++;
                if (!page.Available) return Partial("gps_pings", "route_unavailable", retentionDays);
                retentionDays = Math.Clamp(page.RetentionDays, 1, 30);
                retainedFrom ??= page.RetainedFrom;
                pings.AddRange(page.Pings);
                if (!page.HasMore)
                    return GpsEvidence("complete", null, deliveryId, pings, retentionDays, retainedFrom);
                if (string.IsNullOrWhiteSpace(page.NextCursor))
                    return GpsEvidence("partial", "missing_next_cursor", deliveryId, pings, retentionDays, retainedFrom);
                if (page.Pings.Count == 0)
                    return GpsEvidence("partial", "non_advancing_page", deliveryId, pings, retentionDays, retainedFrom);
                if (!seenCursors.Add(page.NextCursor))
                    return GpsEvidence("partial", "non_advancing_cursor", deliveryId, pings, retentionDays, retainedFrom);
                if (pages >= maxPages)
                    return GpsEvidence("partial", "truncated_max_pages", deliveryId, pings, retentionDays, retainedFrom);
                cursor = page.NextCursor;
            }
            return GpsEvidence("partial", "truncated_max_points", deliveryId, pings, retentionDays, retainedFrom);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return GpsEvidence("partial", "time_budget_exhausted", deliveryId, pings, retentionDays, retainedFrom); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "event=case.evidence_partial source=gps_pings delivery_id={DeliveryId}", deliveryId);
            return pings.Count == 0 ? Partial("gps_pings", "upstream_unavailable", retentionDays)
                : GpsEvidence("partial", "upstream_unavailable", deliveryId, pings, retentionDays, retainedFrom);
        }
    }

    private GenericCaseEvidenceV1 GpsEvidence(string status, string? marker, string trackId,
        IReadOnlyList<GpsTrackHistoryPoint> pings, int retentionDays, DateTimeOffset? retainedFrom) =>
        new()
        {
            Source = "gps_pings", Status = status, Marker = marker, CapturedAt = _clock.GetUtcNow(),
            Count = pings.Count, RetentionDays = retentionDays,
            ExpiresAt = _clock.GetUtcNow().AddDays(retentionDays),
            Payload = JsonSerializer.SerializeToElement(new { trackId, retainedFrom, pings }, Json),
        };

    private GenericCaseEvidenceV1 Evidence(string source, string status, int count,
        string? marker, JsonElement payload)
    {
        if (status != "complete") CaseTelemetry.EvidencePartial.Add(1, new("source", source), new("marker", marker));
        return new GenericCaseEvidenceV1
        { Source = source, Status = status, CapturedAt = _clock.GetUtcNow(), Count = count, Marker = marker, Payload = payload };
    }

    private GenericCaseEvidenceV1 Partial(string source, string marker, int? retentionDays = null)
    {
        CaseTelemetry.EvidencePartial.Add(1, new("source", source), new("marker", marker));
        return new GenericCaseEvidenceV1
        {
            Source = source, Status = "unavailable", CapturedAt = _clock.GetUtcNow(), Marker = marker,
            RetentionDays = retentionDays,
            ExpiresAt = retentionDays is null ? null : _clock.GetUtcNow().AddDays(retentionDays.Value),
        };
    }
}
