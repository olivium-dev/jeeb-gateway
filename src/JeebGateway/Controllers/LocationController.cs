using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Realtime;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// GPS location ingest + tracking snapshot surface (T-backend-014, JEEB-32).
///
/// <list type="bullet">
///   <item>POST /location/update — Jeebers stream a batch of GPS samples;
///     the latest (by device timestamp) is retained per Jeeber in
///     <see cref="ILocationStore"/> with the configured TTL (default 5 min).</item>
///   <item>GET /deliveries/{id}/tracking — ONE-SHOT JSON snapshot of the
///     latest fix plus the straight-line polyline to the dropoff. It reads
///     once and returns; it never holds the connection open.</item>
/// </list>
///
/// <para><b>NO SERVER-SIDE POLLING.</b> This surface used to answer
/// <c>Accept: text/event-stream</c> with a fake stream: a
/// <c>while (!ct.IsCancellationRequested)</c> loop that slept
/// <c>Tracking:SseInterval</c> (5 s) and re-read the gateway's OWN store on
/// every tick to synthesise a "live" feed. That is a poll wearing a stream's
/// clothes — the gateway asking itself "is there anything new?" forever, once
/// per connected client. It is deleted, along with the
/// <c>/v1/geo/jeeb/stream/{id}</c> alias that existed only to open it.</para>
///
/// <para>Propagation is not this layer's job. Per the architecture ruling the
/// backend WRITES and the client SUBSCRIBES: chat state lands in Firestore
/// (chat-service <c>FirestoreConversationStore.AddMessageAsync</c>) and the
/// device's own snapshot listener delivers it. Nothing here — and nothing
/// anywhere in this gateway — opens a Firestore listener or loops asking a
/// datastore for changes. <c>Tracking/NoBackendPollOrFirestoreListenerGuardTests</c>
/// is the standing gate on both halves of that sentence.</para>
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
    private readonly IGeoHistoryClient _geoHistory;
    private readonly IDeliveryParticipantResolver _participants;
    private readonly ICourierPositionQueue _positions;
    private readonly IOptionsMonitor<CourierPositionPublishOptions> _publishOptions;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<LocationController> _logger;

    public LocationController(
        ILocationStore store,
        IRequestsStore requests,
        IOptionsMonitor<TrackingOptions> options,
        TimeProvider clock,
        IDeliveryServiceClient delivery,
        IGeoHistoryClient geoHistory,
        IDeliveryParticipantResolver participants,
        ICourierPositionQueue positions,
        IOptionsMonitor<CourierPositionPublishOptions> publishOptions,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<LocationController> logger)
    {
        _store = store;
        _requests = requests;
        _options = options;
        _clock = clock;
        _delivery = delivery;
        _geoHistory = geoHistory;
        _participants = participants;
        _positions = positions;
        _publishOptions = publishOptions;
        _flags = flags.Value;
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
        var result = await _store.RecordAsync(jeeberId, points, ct);

        var latest = result.Latest is null ? null : new GpsPointDto
        {
            Lat = result.Latest.Lat,
            Lng = result.Latest.Lng,
            Accuracy = result.Latest.Accuracy,
            Timestamp = result.Latest.DeviceTimestamp
        };

        // Delivery-scoped fixes are evidence, not just a latest-position cache. Start
        // persistence under the delivery id in geolocation-service's generic 30-day
        // history now, but await it after the non-blocking realtime enqueue below. This
        // keeps the customer map moving during a normal Retry-After delay without
        // reporting success to the device until this fix is durable.
        var retryingLatestForHistory = latest is not null
            && result.Accepted == 0
            && result.Rejected == 0
            && points.Any(point => SameFix(point, latest));
        Task? historyWrite = null;
        var historyRetryAcknowledged = false;
        if (latest is not null
            && (result.Accepted > 0 || retryingLatestForHistory)
            && !string.IsNullOrWhiteSpace(body?.DeliveryId))
        {
            if (retryingLatestForHistory)
            {
                _logger.LogInformation(
                    "Retrying track-history persistence for locally retained delivery fix {DeliveryId}/{JeeberId} at {RecordedAt}.",
                    body.DeliveryId, jeeberId, latest.Timestamp);
            }
            try
            {
                historyWrite = _geoHistory.RecordTrackPointAsync(
                    body.DeliveryId,
                    jeeberId,
                    latest.Lat,
                    latest.Lng,
                    latest.Accuracy,
                    latest.Timestamp,
                    ct);
            }
            catch (Exception error)
            {
                // Keep even a synchronously-failing dependency implementation on
                // the common await path below so realtime still receives this fix.
                historyWrite = Task.FromException(error);
            }
        }

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

        // Continuous courier position: hand the accepted fix to the realtime fan-out so a
        // watching customer's map moves without anyone polling for it. The store write
        // above is the money path and has already happened; this is strictly additive.
        //
        // FIRE-AND-FORGET, and specifically: a non-blocking TryEnqueue onto a BOUNDED
        // channel drained by CourierPositionPublisher. No await, no I/O, no HttpClient on
        // this thread, and the request's CancellationToken is deliberately NOT passed on —
        // see CourierPositionPublisher's remarks for the four properties that make a
        // realtime outage unable to fail or slow this response.
        if (latest is not null
            && _publishOptions.CurrentValue.Enabled
            && _flags.Realtime
            && CourierPositionTopic.IsSafeDeliveryId(body?.DeliveryId))
        {
            var queued = _positions.TryEnqueue(new CourierPosition(
                DeliveryId: body!.DeliveryId!,
                JeeberId: jeeberId,
                Lat: latest.Lat,
                Lng: latest.Lng,
                Accuracy: latest.Accuracy,
                DeviceTimestamp: latest.Timestamp));

            if (!queued)
            {
                // Observable shedding, not a silent drop. The fix is already durable in
                // the store; only the live fan-out of this one position is lost, and the
                // next fix supersedes it.
                _logger.LogWarning(
                    "Realtime position buffer full ({Pending} pending); position for delivery {DeliveryId} not fanned out.",
                    _positions.PendingCount, body!.DeliveryId);
            }
        }

        if (historyWrite is not null)
        {
            try
            {
                await historyWrite;
                // An exact retry can already exist in the latest-position store while its
                // first history write failed. Once history succeeds, acknowledge that fix
                // to the mobile caller even though it was not inserted locally a second time.
                historyRetryAcknowledged = retryingLatestForHistory;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogError(error,
                    "Delivery track-history write failed for delivery {DeliveryId}, jeeber {JeeberId}; "
                    + "the fix is not acknowledged as durable evidence.",
                    body!.DeliveryId, jeeberId);
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Delivery tracking history is temporarily unavailable.",
                    Detail = "The location update was not acknowledged; retry with the latest position.",
                    Status = StatusCodes.Status502BadGateway,
                    Type = "https://jeeb.dev/errors/tracking-history-unavailable",
                });
            }
        }

        return Ok(new LocationUpdateResponse
        {
            Accepted = historyRetryAcknowledged ? 1 : result.Accepted,
            Rejected = historyRetryAcknowledged ? 0 : result.Rejected,
            Latest = latest
        });
    }

    private static bool SameFix(GpsPointDto submitted, GpsPointDto latest) =>
        submitted.Timestamp == latest.Timestamp
        && submitted.Lat.Equals(latest.Lat)
        && submitted.Lng.Equals(latest.Lng)
        && Nullable.Equals(submitted.Accuracy, latest.Accuracy);

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
    /// Body of the live-track surface. Resolves + authorizes the caller against
    /// the delivery parties (delivery-service is the authority), then returns a
    /// one-shot JSON polyline snapshot (H4/A3). The participant gate runs first,
    /// so a non-party is denied (403 + RFC 7807) before any position is read (N1).
    ///
    /// <para>There is deliberately no <c>Accept</c> branch any more: the
    /// event-stream arm was a 5-second server-side re-read loop and is deleted.
    /// A client that still sends <c>Accept: text/event-stream</c> gets this same
    /// JSON snapshot rather than a held connection — degraded, never wedged.</para>
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

        // One read, one body, connection closed. No Accept branch, no loop.
        await WritePolylineSnapshotAsync(participants, ct);
    }

    /// <summary>
    /// The one-shot JSON polyline body — the whole of this surface now.
    /// Composes the latest Jeeber fix (held in <see cref="ILocationStore"/>)
    /// with the delivery's dropoff into the MVP straight-line route, and
    /// carries the staleness verdict (<c>positionStatus</c> / <c>stale</c> /
    /// <c>secondsSinceUpdate</c>) that used to ride the deleted <c>last-seen</c>
    /// stream event, so the "Jeeber offline" affordance survives the stream's
    /// removal. No held connection; a stable etag lets a repeat read be
    /// conditional (JEB-54 AC3).
    /// </summary>
    /// <remarks>
    /// <b>The phantom courier pin.</b> This body used to compute
    /// <c>Stale = latest is not null &amp;&amp; age &gt; StaleThreshold</c>. Because the
    /// store deleted a fix once it passed <c>Tracking:PositionTtl</c> and returned
    /// null, <c>latest</c> went null exactly when the courier had been missing
    /// LONGEST — and the conjunct above made <c>stale</c> <c>false</c> by
    /// construction. Two opposite states serialised byte-identically
    /// (<c>position:null, stale:false, secondsSinceUpdate:null, polyline:[]</c>),
    /// so a customer's map, which keeps its own marker and waits for
    /// <c>stale:true</c> to degrade it, was told "fine" forever and left a live-
    /// looking pin at a location the courier had left minutes earlier.
    /// <para>The verdict is now derived once, from the fix's age, by
    /// <see cref="TrackingFreshness.Classify"/>, and every field below is a
    /// function of it — so the four states cannot collapse into each other again
    /// without that classifier changing.</para>
    /// </remarks>
    private async Task WritePolylineSnapshotAsync(DeliveryParticipants participants, CancellationToken ct)
    {
        var jeeberId = participants.JeeberId ?? string.Empty;
        var latest = string.IsNullOrEmpty(jeeberId) ? null : await _store.GetLatestAsync(jeeberId, ct);
        var now = _clock.GetUtcNow();

        // ONE verdict, derived once. The store now hands back an old fix instead of
        // destroying it, so `latest is null` genuinely means "nothing on record".
        var freshness = TrackingFreshness.Classify(latest, now, _options.CurrentValue);

        // Past PositionTtl we publish neither the coordinates nor a route drawn
        // from them. That matches what the wire did before this fix (the store had
        // simply thrown the fix away by then), so no client gains a pin it did not
        // have — what changes is that `stale` now tells the truth alongside it.
        var publishable = freshness.PublishesCoordinates() ? latest : null;
        var polyline = Polyline.StraightLine(publishable, participants.DropoffLocation);

        var dto = new TrackingPolylineDto
        {
            DeliveryId = participants.DeliveryId,
            JeeberId = jeeberId,
            Polyline = polyline,
            Position = publishable is null ? null : new GpsPointDto
            {
                Lat = publishable.Lat,
                Lng = publishable.Lng,
                Accuracy = publishable.Accuracy,
                Timestamp = publishable.DeviceTimestamp
            },
            // Additive on the wire; a client that reads none of these three is
            // still strictly better off, because `stale` no longer lies.
            PositionStatus = freshness.ToWireValue(),
            Stale = freshness.IsStale(),
            // Non-null whenever ANY fix is on record — including "lost", where it is
            // the only thing separating a missing courier from one who never
            // started. Measured from `latest`, not `publishable`.
            SecondsSinceUpdate = latest is null ? null : (now - latest.ReceivedAt).TotalSeconds,
            Etag = SnapshotEtag(polyline, freshness),
            ServerTimestamp = now
        };

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/json";
        Response.Headers.ETag = $"\"{dto.Etag}\"";
        await Response.WriteAsync(JsonSerializer.Serialize(dto, JsonOptions), ct);
    }

    /// <summary>
    /// Stable, order-sensitive hash of everything a client would re-render on:
    /// the polyline geometry AND the freshness verdict. Identical snapshots
    /// produce identical etags so a repeat read within the cache window is a
    /// no-op render. Deterministic FNV-1a over the rounded coordinates — no
    /// allocation churn, no dependency on object identity.
    /// </summary>
    /// <remarks>
    /// The freshness verdict is folded in deliberately. Hashing the geometry
    /// ALONE meant the <c>live → stale</c> transition produced an identical etag
    /// (a stationary courier's polyline does not move), so a client skipping a
    /// re-render on an unchanged etag would never learn the marker had gone
    /// stale — the same class of miss as the defect this method's caller fixes.
    /// Only the four-valued verdict is folded in, never
    /// <c>secondsSinceUpdate</c>: hashing a per-second value would churn the etag
    /// every read and destroy its purpose.
    /// </remarks>
    private static string SnapshotEtag(IReadOnlyList<double[]> polyline, PositionFreshness freshness)
    {
        unchecked
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            var hash = fnvOffset;
            hash = (hash ^ (ulong)(int)freshness) * fnvPrime;
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
}
