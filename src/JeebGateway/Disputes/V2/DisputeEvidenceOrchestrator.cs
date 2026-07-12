using JeebGateway.Requests;
using JeebGateway.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// <see cref="IDisputeEvidenceOrchestrator"/>. Captures a stub GPS polyline via
/// <see cref="ILocationStore"/> + delivery pickup/dropoff. The call runs with a
/// strict timeout — an exceeded call degrades the evidence bundle instead of
/// failing the escalate.
///
/// CHAT TRANSCRIPT REMOVED: the gateway no longer carries a chat BFF client (the
/// salehly mirror replaced jeeb's 1:1 conversation BFF with a passthrough
/// ChatController over the generic chat-service). Chat-transcript evidence capture
/// is therefore left empty here; it will be re-wired when chat-service exposes a
/// generic transcript-by-participants read the gateway can call directly. The geo
/// polyline remains a stub pending the geolocation-service relocation.
/// </summary>
public sealed class DisputeEvidenceOrchestrator : IDisputeEvidenceOrchestrator
{
    private readonly ILocationStore _location;
    private readonly IRequestsStore _deliveries;
    private readonly IOptionsMonitor<DisputeEvidenceOptions> _options;
    private readonly ILogger<DisputeEvidenceOrchestrator> _log;

    public DisputeEvidenceOrchestrator(
        ILocationStore location,
        IRequestsStore deliveries,
        IOptionsMonitor<DisputeEvidenceOptions> options,
        ILogger<DisputeEvidenceOrchestrator> log)
    {
        _location = location;
        _deliveries = deliveries;
        _options = options;
        _log = log;
    }

    public async Task<DisputeEvidence> CaptureAsync(DisputeEvidenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opts = _options.CurrentValue;

        // Chat transcript capture is removed (no gateway chat BFF client); GPS
        // polyline remains. Keep the chat tuple shape so the evidence bundle and
        // its degraded-reason aggregation stay stable.
        var chat = CaptureChat();
        var gps = await CaptureGpsAsync(request, opts, ct).ConfigureAwait(false);

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

    private static (string? Json, int MessageCount, bool Degraded, string? Reason) CaptureChat()
    {
        // Chat transcript capture is removed with the gateway chat BFF client. The
        // evidence bundle reports no transcript (empty, not degraded) until
        // chat-service exposes a generic transcript-by-participants read the
        // gateway can call directly.
        return (null, 0, false, null);
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
                var latest = await _location.GetLatestAsync(jeeberId, linked.Token).ConfigureAwait(false);
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
