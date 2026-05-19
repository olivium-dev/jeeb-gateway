using System.Text.Json;
using JeebGateway.Chat;
using JeebGateway.Requests;
using JeebGateway.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// MVP <see cref="IDisputeEvidenceOrchestrator"/>. Pulls the chat
/// transcript via <see cref="IChatMessageStore"/> and a stub polyline
/// via <see cref="ILocationStore"/> + delivery pickup/dropoff. Both
/// calls run with a strict timeout — exceeded calls degrade the
/// evidence bundle instead of failing the escalate.
///
/// In production the chat/geo stores are replaced with NSwag-generated
/// HTTP clients to <c>chat-service</c> and <c>geolocation-service</c>;
/// the orchestrator contract is the same so the controller and tests
/// don't change.
/// </summary>
public sealed class DisputeEvidenceOrchestrator : IDisputeEvidenceOrchestrator
{
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IChatMessageStore _chat;
    private readonly ILocationStore _location;
    private readonly IRequestsStore _deliveries;
    private readonly IOptionsMonitor<DisputeEvidenceOptions> _options;
    private readonly ILogger<DisputeEvidenceOrchestrator> _log;

    public DisputeEvidenceOrchestrator(
        IChatMessageStore chat,
        ILocationStore location,
        IRequestsStore deliveries,
        IOptionsMonitor<DisputeEvidenceOptions> options,
        ILogger<DisputeEvidenceOrchestrator> log)
    {
        _chat = chat;
        _location = location;
        _deliveries = deliveries;
        _options = options;
        _log = log;
    }

    public async Task<DisputeEvidence> CaptureAsync(DisputeEvidenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opts = _options.CurrentValue;

        // Fire both calls in parallel — neither depends on the other and
        // the AC6 1-second open budget assumes they overlap.
        var chatTask = CaptureChatAsync(request, opts, ct);
        var gpsTask = CaptureGpsAsync(request, opts, ct);
        await Task.WhenAll(chatTask, gpsTask).ConfigureAwait(false);

        var chat = chatTask.Result;
        var gps = gpsTask.Result;

        var degraded = chat.Degraded || gps.Degraded;
        var reason = (chat.Degraded, gps.Degraded) switch
        {
            (true, true) => $"chat:{chat.Reason}; gps:{gps.Reason}",
            (true, false) => $"chat:{chat.Reason}",
            (false, true) => $"gps:{gps.Reason}",
            _ => null
        };

        return new DisputeEvidence
        {
            ChatTranscriptJson = chat.Json,
            ChatTranscriptMessageCount = chat.MessageCount,
            GpsPolyline = gps.Polyline,
            Degraded = degraded,
            DegradedReason = reason
        };
    }

    private async Task<(string? Json, int MessageCount, bool Degraded, string? Reason)> CaptureChatAsync(
        DisputeEvidenceRequest request,
        DisputeEvidenceOptions opts,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.CounterpartyUserId))
        {
            return (null, 0, false, null);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(opts.ChatFetchTimeout);

        try
        {
            var conversationId = ConversationKey.For(request.OpenedByUserId, request.CounterpartyUserId);
            var messages = await _chat
                .GetByConversationAsync(conversationId, opts.MaxTranscriptMessages, linked.Token)
                .ConfigureAwait(false);

            var snapshot = messages.Select(m => new
            {
                id = m.Id,
                conversation_id = m.ConversationId,
                sender_id = m.SenderId,
                recipient_id = m.RecipientId,
                type = m.Type.ToString(),
                sent_at = m.SentAt,
                text = m.Text,
                media_url = m.MediaUrl,
                latitude = m.Latitude,
                longitude = m.Longitude,
                offer_id = m.OfferId,
                read_at = m.ReadAt
            }).ToList();

            var json = JsonSerializer.Serialize(snapshot, TranscriptJsonOptions);
            return (json, messages.Count, false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("dispute evidence: chat transcript fetch timed out after {Timeout}", opts.ChatFetchTimeout);
            return (null, 0, true, "timeout");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dispute evidence: chat transcript fetch failed");
            return (null, 0, true, ex.GetType().Name);
        }
    }

    private async Task<(IReadOnlyList<double[]> Polyline, bool Degraded, string? Reason)> CaptureGpsAsync(
        DisputeEvidenceRequest request,
        DisputeEvidenceOptions opts,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(opts.GeoFetchTimeout);

        try
        {
            var delivery = await _deliveries.GetAsync(request.DeliveryId, linked.Token).ConfigureAwait(false);

            var points = new List<double[]>(3);
            if (delivery?.PickupLocation is { } pickup && pickup.IsValid())
            {
                points.Add(new[] { pickup.Lat, pickup.Lng });
            }

            // Production wiring: replace with geolocation-service.RoutePolylineAsync
            // for the full ping history; MVP fold-down uses the latest fix.
            var jeeberId = request.JeeberId ?? delivery?.JeeberId;
            if (!string.IsNullOrEmpty(jeeberId))
            {
                var latest = _location.GetLatest(jeeberId);
                if (latest is not null)
                {
                    points.Add(new[] { latest.Lat, latest.Lng });
                }
            }

            if (delivery?.DropoffLocation is { } dropoff && dropoff.IsValid())
            {
                points.Add(new[] { dropoff.Lat, dropoff.Lng });
            }

            return (points, false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("dispute evidence: gps polyline fetch timed out after {Timeout}", opts.GeoFetchTimeout);
            return (Array.Empty<double[]>(), true, "timeout");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dispute evidence: gps polyline fetch failed");
            return (Array.Empty<double[]>(), true, ex.GetType().Name);
        }
    }
}
