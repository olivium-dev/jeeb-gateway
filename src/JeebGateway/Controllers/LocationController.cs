using System.Text.Json;
using JeebGateway.Requests;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// GPS location ingest + SSE tracking surface (T-backend-014, JEEB-32).
///
/// <list type="bullet">
///   <item>POST /location/update — Jeebers stream a batch of GPS samples;
///     the latest (by device timestamp) is retained per Jeeber in
///     <see cref="ILocationStore"/> with the configured TTL (default 5 min).</item>
///   <item>GET /deliveries/{id}/tracking — Server-Sent Events stream
///     emitting a position frame every <see cref="TrackingOptions.SseInterval"/>
///     (default 5 s). When the latest fix is older than
///     <see cref="TrackingOptions.StaleThreshold"/> (default 2 min) the
///     event name switches to <c>last-seen</c> so the client can render
///     the "Jeeber offline" affordance.</item>
/// </list>
///
/// The SSE loop is intentionally minimal — no buffering, no per-client
/// queue. Each tick reads the in-memory store (lock-free) and writes a
/// single SSE record. That keeps the per-connection cost at one timer
/// and one dictionary lookup, which scales to thousands of concurrent
/// tracking sessions without blocking the location ingest path.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
public class LocationController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILocationStore _store;
    private readonly IRequestsStore _requests;
    private readonly IOptionsMonitor<TrackingOptions> _options;
    private readonly TimeProvider _clock;

    public LocationController(
        ILocationStore store,
        IRequestsStore requests,
        IOptionsMonitor<TrackingOptions> options,
        TimeProvider clock)
    {
        _store = store;
        _requests = requests;
        _options = options;
        _clock = clock;
    }

    [HttpPost("location/update")]
    [ProducesResponseType(typeof(LocationUpdateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Update([FromBody] LocationUpdateRequest? body)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized)) return unauthorized;

        if (body is null || body.Points is null || body.Points.Count == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "points is required and must not be empty.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/empty-batch"
            });
        }

        var max = _options.CurrentValue.MaxPointsPerBatch;
        if (body.Points.Count > max)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"batch exceeds the per-request cap of {max} points.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/batch-too-large"
            });
        }

        var result = _store.Record(jeeberId, body.Points);

        return Ok(new LocationUpdateResponse
        {
            Accepted = result.Accepted,
            Rejected = result.Rejected,
            Latest = result.Latest is null ? null : new GpsPointDto
            {
                Lat = result.Latest.Lat,
                Lng = result.Latest.Lng,
                Accuracy = result.Latest.Accuracy,
                Timestamp = result.Latest.DeviceTimestamp
            }
        });
    }

    [HttpGet("deliveries/{deliveryId}/tracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task TrackAsync(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out _))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Only the two parties bound to the delivery (the Client who
        // owns it and the Jeeber assigned to fulfil it) may subscribe to
        // the live track. Admins are exempt — they need a live view for
        // ops triage.
        var isParticipant = string.Equals(delivery.ClientId, userId, StringComparison.Ordinal)
            || string.Equals(delivery.JeeberId, userId, StringComparison.Ordinal);
        if (!isParticipant && !UserIdentity.IsAdmin(HttpContext))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Send an immediate frame so the client doesn't sit on an empty
        // stream for the first interval — important for "awaiting first
        // ping" UX when the Jeeber hasn't reported yet.
        await EmitFrameAsync(delivery, ct);

        var interval = _options.CurrentValue.SseInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _clock, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            // Re-read the delivery so a status flip to a terminal state
            // (delivered / rated / cancelled) cleanly ends the stream
            // instead of streaming forever after the trip ends.
            var current = await _requests.GetAsync(deliveryId, ct);
            if (current is null || RequestStatus.IsTerminal(current.Status))
            {
                break;
            }
            await EmitFrameAsync(current, ct);
        }
    }

    private async Task EmitFrameAsync(DeliveryRequest delivery, CancellationToken ct)
    {
        var jeeberId = delivery.JeeberId ?? string.Empty;
        var latest = string.IsNullOrEmpty(jeeberId) ? null : _store.GetLatest(jeeberId);
        var now = _clock.GetUtcNow();

        double? sinceSec = latest is null
            ? null
            : (now - latest.ReceivedAt).TotalSeconds;
        var stale = latest is not null
            && (now - latest.ReceivedAt) > _options.CurrentValue.StaleThreshold;

        var frame = new TrackingFrameDto
        {
            DeliveryId = delivery.Id,
            JeeberId = jeeberId,
            Position = latest is null ? null : new GpsPointDto
            {
                Lat = latest.Lat,
                Lng = latest.Lng,
                Accuracy = latest.Accuracy,
                Timestamp = latest.DeviceTimestamp
            },
            Polyline = Polyline.StraightLine(latest, delivery.DropoffLocation),
            Stale = stale,
            SecondsSinceUpdate = sinceSec,
            ServerTimestamp = now
        };

        var eventName = stale ? "last-seen" : "position";
        var json = JsonSerializer.Serialize(frame, JsonOptions);

        // SSE wire format: `event: <name>\ndata: <json>\n\n`. Multi-line
        // JSON is fine because we serialize without indentation, so the
        // single data: prefix carries the entire payload.
        await Response.WriteAsync($"event: {eventName}\n", ct);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
