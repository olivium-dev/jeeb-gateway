using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
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
    private readonly IDeliveryServiceClient _delivery;
    private readonly IDeliveryParticipantResolver _participants;
    private readonly ILogger<LocationController> _logger;

    public LocationController(
        ILocationStore store,
        IRequestsStore requests,
        IOptionsMonitor<TrackingOptions> options,
        TimeProvider clock,
        IDeliveryServiceClient delivery,
        IDeliveryParticipantResolver participants,
        ILogger<LocationController> logger)
    {
        _store = store;
        _requests = requests;
        _options = options;
        _clock = clock;
        _delivery = delivery;
        _participants = participants;
        _logger = logger;
    }

    [HttpPost("location/update")]
    // ADR-005 L2 §D jeeber-only: a jeeber streams their GPS batch (caller resolved as jeeberId).
    [RequireCapability(Capabilities.DeliveryGpsStream)]
    [ProducesResponseType(typeof(LocationUpdateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Update([FromBody] LocationUpdateRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized)) return unauthorized;

        // S09 (JEB-54) single-point shape: {deliveryId, lat, lng}. Normalise it
        // into the canonical batch so the rest of the path is shape-agnostic. The
        // device timestamp defaults to the server clock — the single-point client
        // does not carry its own fix time.
        var points = body?.Points;
        if (body is not null && (points is null || points.Count == 0)
            && body.Lat.HasValue && body.Lng.HasValue)
        {
            points = new List<GpsPointDto>
            {
                new GpsPointDto
                {
                    Lat = body.Lat.Value,
                    Lng = body.Lng.Value,
                    Accuracy = body.Accuracy,
                    Timestamp = _clock.GetUtcNow()
                }
            };
        }

        // S09 delivery-scoped authz (N2/N5): when the request names a delivery,
        // resolve the parties via delivery-service and gate BEFORE recording the
        // fix or touching any geolocation surface. delivery-service is the
        // authority for "who is a party" and "is the trip in_transit"; the
        // gateway composes that verdict — no cross-service DB read, no coupling.
        if (body is not null && !string.IsNullOrWhiteSpace(body.DeliveryId))
        {
            var delivery = await _participants.ResolveAsync(body.DeliveryId, ct);
            if (delivery is null)
            {
                return NotFound();
            }

            // N2: a non-party (e.g. Rana, removed at S07-accept, or an outsider)
            // is denied before any gps_pings write. Admins do not ingest GPS —
            // only the assigned Jeeber streams fixes, so no admin exemption here.
            if (!delivery.IsParty(jeeberId))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "You are not a party to this delivery.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/location-not-a-party"
                });
            }

            // N5/E4: the ingest lifecycle gate. A ping is only accepted while the
            // trip is en route (InTransit). Before pickup or after AtDoor/Done the
            // gateway refuses on the delivery-service status WITHOUT touching
            // geolocation (no gps_pings write, no upstream call).
            if (!delivery.IsInTransit)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Delivery is not in the en-route phase; location updates are not accepted.",
                    Detail = $"Current status: {delivery.Status}",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/location-not-in-transit"
                });
            }
        }

        if (points is null || points.Count == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "points is required and must not be empty.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/empty-batch"
            });
        }

        var max = _options.CurrentValue.MaxPointsPerBatch;
        if (points.Count > max)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"batch exceeds the per-request cap of {max} points.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/batch-too-large"
            });
        }

        // Record into the in-memory store: this computes accepted/rejected +
        // the device-latest fix and backs the SSE tracking read path
        // (GET /deliveries/{id}/tracking), which has no upstream equivalent in
        // S06 scope.
        var result = _store.Record(jeeberId, points);

        var latest = result.Latest is null ? null : new GpsPointDto
        {
            Lat = result.Latest.Lat,
            Lng = result.Latest.Lng,
            Accuracy = result.Latest.Accuracy,
            Timestamp = result.Latest.DeviceTimestamp
        };

        // S06 keystone: forward the latest accepted fix to the canonical
        // delivery-service presence store as a heartbeat. This bumps
        // last_heartbeat_at + last-known location in the SAME store the matching
        // run reads for its freshness predicate — so a streaming GPS jeeber stays
        // in the online set (A2). Routed to delivery-service, NOT geolocation, to
        // keep ONE presence store (org-law: no cross-service DB read).
        if (latest is not null)
        {
            try
            {
                await _delivery.HeartbeatAsync(jeeberId, latest.Lat, latest.Lng, ct);
            }
            catch (DeliveryAvailabilityException ex)
            {
                // A heartbeat for a jeeber who never went online (404) must not
                // 500 the GPS ingest path. The in-memory store already retained
                // the fix for tracking; the presence bump is best-effort. Log and
                // continue — the response shape is unchanged.
                _logger.LogWarning(
                    "delivery-service presence heartbeat for jeeber {JeeberId} returned {Status} ({Reason}); GPS fix retained locally, presence not bumped.",
                    jeeberId, ex.StatusCode, ex.Reason);
            }
            catch (HttpRequestException ex)
            {
                // A transport-level failure to reach delivery-service (connection
                // reset, DNS, timeout) must ALSO not 500 the GPS ingest path
                // (S06 A2 requires 200). The presence heartbeat is strictly
                // best-effort and additive to the in-memory fix already retained
                // for the SSE tracking read; surface nothing to the caller. This
                // widens the existing best-effort net from the typed presence
                // error to the underlying transport error without changing the
                // happy-path response shape.
                _logger.LogWarning(
                    ex,
                    "delivery-service presence heartbeat for jeeber {JeeberId} failed at transport level; GPS fix retained locally, presence not bumped.",
                    jeeberId);
            }
        }

        return Ok(new LocationUpdateResponse
        {
            Accepted = result.Accepted,
            Rejected = result.Rejected,
            Latest = latest
        });
    }

    [HttpGet("deliveries/{deliveryId}/tracking")]
    // ADR-005 L2 §C client-only delivery tracking (STATE: party-on-delivery/ownership stays in-action).
    [RequireCapability(Capabilities.DeliveryTrackOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task TrackAsync(string deliveryId, CancellationToken ct)
        => TrackOrPolylineAsync(deliveryId, ct);

    /// <summary>
    /// S09 (JEB-54) SSE alias. The mobile live-map screen subscribes here with
    /// <c>Accept: text/event-stream</c>. It is the participant-gated bridge to
    /// the live track — identical authorization + stream as
    /// <c>GET /deliveries/{id}/tracking</c>, exposed under the
    /// <c>/v1/geo/jeeb/stream/{id}</c> path the client SDK calls. The gateway
    /// resolves participants via delivery-service and DENIES a non-party
    /// (403, N1) BEFORE opening the upstream geo:track subscription — the
    /// socket never receives a position frame. Composition lives only here:
    /// delivery-service owns the parties, geolocation owns the pings.
    /// </summary>
    [HttpGet("v1/geo/jeeb/stream/{deliveryId}")]
    [RequireCapability(Capabilities.DeliveryTrackOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task StreamAsync(string deliveryId, CancellationToken ct)
        => TrackOrPolylineAsync(deliveryId, ct);

    /// <summary>
    /// Shared body for the live-track surface. Resolves + authorizes the caller
    /// against the delivery parties (delivery-service is the authority), then
    /// content-negotiates: <c>Accept: text/event-stream</c> ⇒ the SSE relay;
    /// otherwise a one-shot JSON polyline snapshot (H4/A3). The participant gate
    /// runs once, BEFORE either branch, so a non-party is denied before any
    /// stream is opened (N1).
    /// </summary>
    private async Task TrackOrPolylineAsync(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out _))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var participants = await _participants.ResolveAsync(deliveryId, ct);
        if (participants is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Only the two parties bound to the delivery (the Client who owns it and
        // the Jeeber assigned to fulfil it) may read the live track. Admins are
        // exempt — they need a live view for ops triage (BR-TRK-1).
        if (!participants.IsParty(userId) && !UserIdentity.IsAdmin(HttpContext))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            // RFC 7807 body so the client renders a typed "not a party" error (N1).
            Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails
            {
                Title = "You are not a party to this delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/tracking-not-a-party"
            };
            await Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions), ct);
            return;
        }

        // Content negotiation (H4/A3): a non-SSE Accept gets the JSON polyline
        // snapshot instead of an open stream. This is the "polyline replay /
        // route screen" view — one read, no held connection.
        var accept = Request.Headers.Accept.ToString();
        var wantsSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (!wantsSse)
        {
            await WritePolylineSnapshotAsync(participants, ct);
            return;
        }

        await StreamSseAsync(deliveryId, participants, ct);
    }

    /// <summary>
    /// One-shot JSON polyline body for <c>Accept != text/event-stream</c>.
    /// Composes the latest Jeeber fix (held in <see cref="ILocationStore"/>)
    /// with the delivery's dropoff into the same MVP straight-line route the
    /// SSE position frame carries. No held connection, no upstream Directions
    /// hit beyond what the SSE path already does; a stable etag lets a repeat
    /// read be conditional (JEB-54 AC3).
    /// </summary>
    private async Task WritePolylineSnapshotAsync(DeliveryParticipants participants, CancellationToken ct)
    {
        var jeeberId = participants.JeeberId ?? string.Empty;
        var latest = string.IsNullOrEmpty(jeeberId) ? null : _store.GetLatest(jeeberId);
        var polyline = Polyline.StraightLine(latest, participants.DropoffLocation);

        var dto = new TrackingPolylineDto
        {
            DeliveryId = participants.DeliveryId,
            JeeberId = jeeberId,
            Polyline = polyline,
            Position = latest is null ? null : new GpsPointDto
            {
                Lat = latest.Lat,
                Lng = latest.Lng,
                Accuracy = latest.Accuracy,
                Timestamp = latest.DeviceTimestamp
            },
            Etag = PolylineEtag(polyline),
            ServerTimestamp = _clock.GetUtcNow()
        };

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/json";
        Response.Headers.ETag = $"\"{dto.Etag}\"";
        await Response.WriteAsync(JsonSerializer.Serialize(dto, JsonOptions), ct);
    }

    /// <summary>
    /// Stable, order-sensitive hash of the polyline geometry. Identical routes
    /// produce identical etags so a repeat read within the cache window is a
    /// no-op render. Deterministic FNV-1a over the rounded coordinates — no
    /// allocation churn, no dependency on object identity.
    /// </summary>
    private static string PolylineEtag(IReadOnlyList<double[]> polyline)
    {
        unchecked
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            var hash = fnvOffset;
            foreach (var pt in polyline)
            {
                foreach (var coord in pt)
                {
                    // 6 dp ≈ 0.1 m — finer than GPS noise, coarse enough that
                    // float wobble doesn't churn the etag.
                    var rounded = Math.Round(coord, 6);
                    var bits = (ulong)BitConverter.DoubleToInt64Bits(rounded);
                    hash = (hash ^ bits) * fnvPrime;
                }
            }
            return hash.ToString("x16");
        }
    }

    private async Task StreamSseAsync(string deliveryId, DeliveryParticipants participants, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Send an immediate frame so the client doesn't sit on an empty
        // stream for the first interval — important for "awaiting first
        // ping" UX when the Jeeber hasn't reported yet.
        await EmitFrameAsync(participants, ct);

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
            // Re-resolve the delivery so a status flip to a terminal state
            // (Done / delivered / rated / cancelled) cleanly ends the stream
            // instead of streaming forever after the trip ends. Uses the same
            // resolver as the gate so the SSE loop and the authz path agree on
            // the canonical-vs-mirror source.
            var current = await _participants.ResolveAsync(deliveryId, ct);
            if (current is null || IsTerminalStatus(current.Status))
            {
                break;
            }
            await EmitFrameAsync(current, ct);
        }
    }

    /// <summary>
    /// Terminal-status predicate spanning both vocabularies: the canonical
    /// SM-1 <c>Done</c> and the legacy mirror terminal set (delivered / rated /
    /// cancelled / expired). Closing the stream on either keeps the SSE loop
    /// correct regardless of which source answered the resolve.
    /// </summary>
    private static bool IsTerminalStatus(string status) =>
        string.Equals(status, "Done", StringComparison.OrdinalIgnoreCase)
        || RequestStatus.IsTerminal(status);

    private async Task EmitFrameAsync(DeliveryParticipants delivery, CancellationToken ct)
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
            DeliveryId = delivery.DeliveryId,
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
