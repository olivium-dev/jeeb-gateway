using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeber availability toggle (T-backend-023, S06 keystone).
///
/// <para>
/// <b>S06 presence wire.</b> The PATCH/GET availability surface is wired
/// THROUGH to the canonical delivery-service presence store via
/// <see cref="IDeliveryServiceClient"/> — the SAME store the matching run reads
/// its online set from (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8). This connects
/// presence to matching: a jeeber who toggles online here becomes a real
/// matching candidate. The upstream write is the source of truth for matching.
/// </para>
/// <para>
/// The gateway ALSO mirrors the toggle into the in-memory
/// <see cref="IAvailabilityStore"/>. That store is NOT a second presence
/// source-of-truth for matching — it backs two gateway-local read surfaces that
/// have no upstream equivalent yet: the admin ops-map
/// (<c>GET /admin/zones/online-jeebers</c>, T-backend-051) and the auto-offline
/// sweeper / withdrawn-offer accounting (T-backend-023). Removing the in-memory
/// store is tracked as a fast-follow that must FIRST relocate those two reads
/// onto delivery-service; deleting it now would break a live admin route and is
/// out of S06 scope (see PR notes / blocker).
/// </para>
/// <para>
/// <b>S06 / ADR-HB-001 — heart-beat cutover (flag-gated, additive).</b> When
/// <c>FeatureFlags:Heartbeat:Enabled</c> is true the GET/PATCH presence
/// read+write route through the NEW reusable <c>heart-beat</c> presence service
/// (<see cref="IHeartBeatServiceClient"/>) instead of delivery-service. The
/// public response shape is byte-identical either way, so no S06 assertion shifts
/// and S01–S04 are untouched. Default is OFF this round (heart-beat not yet
/// deployed); the delivery-service path is the live path AND the instant rollback
/// target. Deploy flips the flag after heart-beat is live and smoke-passed.
/// </para>
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("jeebers/me/availability")]
// GAP-1 (sprint-002, contract-freeze §2 / §2.5): ADDITIVE v1 alias so the mobile
// app's PUT/GET /v1/jeebers/me/availability resolves. It previously 404'd because
// the app calls the v1-prefixed path with PUT, while the gateway only registered
// the un-prefixed path with PATCH. Both [Route]s + both verbs (PUT canonical,
// PATCH back-compat) funnel into the SAME PatchCore — a non-breaking alias, not a
// route move. The 13 existing AvailabilityEndpointTests on the un-prefixed path
// stay green. PUT is the canonical frozen surface (contract-freeze §2.5).
[Route("v1/jeebers/me/availability")]
// ADR-005 L2 §D jeeber-only: class-level (both read + toggle of own availability are jeeber-typed).
// Replaces class-level [RequireRole(Roles.Jeeber)].
[RequireCapability(Capabilities.AvailabilityToggle)]
public class AvailabilityController : ControllerBase
{
    private readonly IAvailabilityStore _store;
    private readonly IDeliveryServiceClient _delivery;
    private readonly IHeartBeatServiceClient _heartBeat;
    private readonly HeartbeatFeatureOptions _heartbeatOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<AvailabilityController> _logger;

    public AvailabilityController(
        IAvailabilityStore store,
        IDeliveryServiceClient delivery,
        IHeartBeatServiceClient heartBeat,
        IOptions<HeartbeatFeatureOptions> heartbeatOptions,
        TimeProvider clock,
        ILogger<AvailabilityController> logger)
    {
        _store = store;
        _delivery = delivery;
        _heartBeat = heartBeat;
        _heartbeatOptions = heartbeatOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        // S06 / ADR-HB-001: when the heart-beat cutover flag is on, read presence
        // from heart-beat; otherwise (default) read from the canonical
        // delivery-service store. Both map onto the SAME public response shape.
        //
        // iter5 BATCHED-FIX B9 — GET availability is documented to "never 500": a
        // never-online jeeber (upstream 404 → null) yields OfflineDefault. The typed
        // clients only translate 404→null; ANY OTHER upstream fault (502/timeout/
        // HeartBeatPresenceException — unhandled on the GET path) previously bubbled
        // as a 5xx and dead-ended the jeeber home. Wrap the presence read so any
        // upstream fault degrades to OfflineDefault 200, mirroring the 404 contract.
        AvailabilityResponse response;
        try
        {
            if (_heartbeatOptions.Enabled)
            {
                var presence = await _heartBeat.GetPresenceAsync(userId, ct);
                response = presence is null ? OfflineDefault(userId) : ToResponse(presence, withdrawnOffers: 0);
            }
            else
            {
                var upstream = await _delivery.GetAvailabilityAsync(userId, ct);
                response = upstream is null ? OfflineDefault(userId) : ToResponse(upstream, withdrawnOffers: 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Availability GET upstream presence read faulted for jeeber {JeeberId}; degrading to OfflineDefault 200 (GET never-500 contract).",
                userId);
            response = OfflineDefault(userId);
        }

        // Mirror the interaction watermark into the in-memory store so the
        // gateway-local auto-offline sweeper (no upstream equivalent yet) still
        // sees this read as activity. Best-effort: a store blip must NOT 500 a
        // successful presence read.
        await TryMirrorAsync(
            () => _store.RecordInteractionAsync(userId, _clock.GetUtcNow(), ct),
            userId,
            "record-interaction");

        return Ok(response);
    }

    // GAP-1 (sprint-002): the mobile app sends PUT; keep PATCH for any existing
    // consumer. Both verbs map to the same PatchCore() — additive, no behaviour change.
    [HttpPut]
    [HttpPatch]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Patch([FromBody] AvailabilityPatchRequest? body, CancellationToken ct)
    {
        // S06 / N14 (NFR-6, BR-X-4): heart-beat owns the per-user presence-toggle
        // rate-limit (Redis token-bucket on PATCH /v1/presence) and returns
        // 429 + Retry-After once a single user exceeds the per-minute budget. The
        // gateway is thin on this path: it FORWARDS the upstream status and the
        // Retry-After header verbatim rather than swallowing the non-2xx into a 500.
        // Without this catch the typed client's HeartBeatPresenceException would
        // propagate as an unhandled 500 (no global mapper exists), so the throttle
        // would never surface as a 429 to the caller.
        try
        {
            return await PatchCore(body, ct);
        }
        catch (HeartBeatPresenceException ex)
        {
            return ForwardHeartBeatError(ex);
        }
    }

    // -----------------------------------------------------------------------
    // iter5 BATCHED-FIX B8 — mobile availability aliases. The installed APK
    // (cfc8920) calls a flat /v1/availability surface that did NOT exist on the
    // gateway (404 EMPTY), so the jeeber-home availability toggle was fully dead:
    //   GET  /v1/availability/{jeeberId}      → read the caller's presence
    //   POST /v1/availability {userId,available} → toggle online/offline
    // Both alias onto the SAME presence logic as jeebers/me/availability. Identity
    // is ALWAYS the bearer (the {jeeberId}/userId in the path/body is informational
    // and never trusted — N: no body-identity). The POST is a thin body adapter:
    // it maps available→online and, when going online with no vehicle/zone supplied
    // (the flat contract carries neither), DEFAULTS them so the upstream PATCH (which
    // requires vehicleType+zone to go online) does not 400. Going offline needs no
    // such defaults.
    // -----------------------------------------------------------------------

    [HttpGet("/v1/availability/{jeeberId}")]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> GetV1Alias(string jeeberId, CancellationToken ct) => Get(ct);

    [HttpPost("/v1/availability")]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> PostV1Alias([FromBody] AvailabilityFlatToggleRequest? body, CancellationToken ct)
    {
        // available→online; default vehicle/zone on go-online so PatchCore does not 400.
        var online = body?.Available ?? body?.Online;
        var adapted = new AvailabilityPatchRequest
        {
            Online = online,
            VehicleType = string.IsNullOrWhiteSpace(body?.VehicleType)
                ? (online is true ? VehicleType.Car.ToWire() : null)
                : body!.VehicleType,
            Zone = string.IsNullOrWhiteSpace(body?.Zone)
                ? (online is true ? "default" : null)
                : body!.Zone,
            Longitude = body?.Longitude,
            Latitude = body?.Latitude,
        };

        // Reuse the SAME wrapped Patch path so heart-beat 429 forwarding + B10
        // never-500 offline degradation apply identically to the alias.
        return Patch(adapted, ct);
    }

    private async Task<IActionResult> PatchCore(AvailabilityPatchRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null || body.Online is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Field 'online' is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Online is true)
        {
            if (!VehicleTypeExtensions.TryParseWire(body.VehicleType, out var vehicle))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Field 'vehicleType' is required to go online.",
                    Detail = "Allowed values: car, motorbike, bicycle, scooter, walk.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            if (string.IsNullOrWhiteSpace(body.Zone))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Field 'zone' is required to go online.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            var zone = body.Zone!.Trim();

            // S06 keystone: write presence to the canonical presence store FIRST so
            // the matching run sees this jeeber as a candidate. heart-beat (when the
            // cutover flag is on) or delivery-service (default) is the source of
            // truth; both return the SAME public response shape.
            AvailabilityResponse onlineResponse;
            if (_heartbeatOptions.Enabled)
            {
                var presence = await _heartBeat.SetPresenceAsync(new HeartBeatPresenceRequest
                {
                    UserId = userId,
                    Online = true,
                    // Opaque consumer namespace — heart-beat never interprets it.
                    RoleKey = _heartbeatOptions.RoleKey,
                    Lat = body.Latitude,
                    Lng = body.Longitude
                }, ct);

                // heart-beat owns ONLY presence; vehicle/zone are Jeeb semantics the
                // gateway echoes back from the request so the response shape (which
                // requires vehicleType + zone) is unchanged.
                onlineResponse = ToResponse(presence, vehicle.ToWire(), zone, withdrawnOffers: 0);
            }
            else
            {
                var upstream = await _delivery.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
                {
                    Online = true,
                    VehicleType = vehicle.ToWire(),
                    Zone = zone,
                    Lat = body.Latitude,
                    Lng = body.Longitude
                }, userId, ct);

                onlineResponse = ToResponse(upstream, withdrawnOffers: 0);
            }

            // Mirror into the gateway-local store that backs the admin ops-map +
            // auto-offline sweeper (no upstream read equivalent yet). Best-effort:
            // a store blip must NOT 500 a successful go-online toggle whose
            // authoritative upstream write already committed.
            await TryMirrorAsync(
                () => _store.GoOnlineAsync(userId, new GoOnlineRequest
                {
                    VehicleType = vehicle,
                    Zone = zone,
                    Longitude = body.Longitude,
                    Latitude = body.Latitude
                }, ct),
                userId,
                "go-online-mirror");

            return Ok(onlineResponse);
        }

        // Offline path. Write the offline transition upstream FIRST (this is the
        // N13 fix: the offline path no longer depends on the in-memory
        // GoOfflineAsync as its primary writer). heart-beat (when the cutover flag
        // is on) or delivery-service (default) is the authoritative offline writer;
        // both return the SAME public response shape. The offline shape carries no
        // vehicle/zone — they are cleared.
        //
        // The withdrawn-offer count is computed from the BEST-EFFORT gateway-local
        // mirror BEFORE building the response so it can be set on the (init-only)
        // response shape. N13 FIX: that mirror is now wrapped — previously the
        // unguarded GoOfflineAsync (which fans out withdraw-offer side-effects to
        // offer-service) could throw and turn a successful offline toggle — whose
        // authoritative upstream write ALREADY committed — into a 500. On mirror
        // failure the withdrawn-offer count degrades to 0 (a count S06 does not
        // assert on); the toggle still returns 200.
        // iter5 BATCHED-FIX B10 — go-offline must mirror Get's never-500 contract.
        // The authoritative offline write (heart-beat / delivery-service) previously
        // threw (e.g. the go-offline write into the presence store faulting) and
        // surfaced as a raw 500, breaking the toggle. Best-effort the upstream offline
        // write: on a fault we still return OfflineDefault 200 (the user's intent —
        // go offline — is honoured at the gateway surface; the upstream re-converges
        // on the next heartbeat/read). A 429 throttle is NOT swallowed here — it is a
        // HeartBeatPresenceException that the Patch wrapper forwards verbatim (N14).
        AvailabilityResponse offlineResponse;
        try
        {
            if (_heartbeatOptions.Enabled)
            {
                var presence = await _heartBeat.SetPresenceAsync(new HeartBeatPresenceRequest
                {
                    UserId = userId,
                    Online = false,
                    RoleKey = _heartbeatOptions.RoleKey
                }, ct);
                var withdrawn = await TryGoOfflineMirrorAsync(userId);
                offlineResponse = ToResponse(presence, withdrawnOffers: withdrawn);
            }
            else
            {
                var offlineUpstream = await _delivery.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
                {
                    Online = false
                }, userId, ct);
                var withdrawn = await TryGoOfflineMirrorAsync(userId);
                offlineResponse = ToResponse(offlineUpstream, withdrawnOffers: withdrawn);
            }
        }
        catch (HeartBeatPresenceException)
        {
            // Let the Patch wrapper forward an upstream 429/Retry-After verbatim (N14).
            throw;
        }
        catch (Exception ex)
        {
            // B10 never-500 contract: map an upstream offline-write fault to the
            // documented OfflineDefault 200. This is ERROR MAPPING only — NO
            // gateway-local fallback store is written on this fault path (the
            // in-memory mirror is deliberately NOT invoked here), so the gateway
            // stays a thin BFF and fabricates no presence state. The authoritative
            // upstream re-converges on the next heartbeat/read.
            _logger.LogWarning(ex,
                "Go-offline upstream write faulted for jeeber {JeeberId}; degrading to OfflineDefault 200 (never-500 contract, no local fallback write).",
                userId);
            offlineResponse = OfflineDefault(userId);
        }

        return Ok(offlineResponse);
    }

    /// <summary>
    /// Runs the gateway-local offline mirror (admin ops-map drop + withdrawn-offer
    /// accounting / fan-out) as a BEST-EFFORT side-effect. Returns the withdrawn
    /// count on success, or 0 if the mirror threw. N13: the authoritative offline
    /// write has already committed upstream by the time this is called, so a mirror
    /// failure must never surface as a 500 on a successful offline toggle.
    /// </summary>
    private async Task<int> TryGoOfflineMirrorAsync(string userId)
    {
        try
        {
            var offlineLocal = await _store.GoOfflineAsync(userId, GoOfflineReason.UserToggle, HttpContext.RequestAborted);
            return offlineLocal.WithdrawnOffers;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Best-effort offline mirror (go-offline-mirror) failed for jeeber {JeeberId}; "
                + "the authoritative offline write already committed, surfacing 200 with 0 withdrawn offers.",
                userId);
            return 0;
        }
    }

    /// <summary>
    /// S06 / N14: maps a non-2xx heart-beat presence outcome onto a ProblemDetails
    /// that FORWARDS the upstream status verbatim (thin BFF). For a throttled
    /// toggle (429) it also re-emits the upstream <c>Retry-After</c> header so the
    /// caller gets the same backoff hint heart-beat's token-bucket computed. A
    /// throttled toggle never mutated presence upstream (heart-beat rejects before
    /// touching state), so forwarding the status is correct — no compensating write.
    /// </summary>
    private IActionResult ForwardHeartBeatError(HeartBeatPresenceException ex)
    {
        if (ex.StatusCode == StatusCodes.Status429TooManyRequests && !string.IsNullOrWhiteSpace(ex.RetryAfter))
        {
            // Re-emit the upstream Retry-After verbatim (whole seconds).
            Response.Headers.RetryAfter = ex.RetryAfter;
        }

        _logger.LogInformation(
            "Forwarding heart-beat presence {StatusCode} ({Reason}) to caller.",
            ex.StatusCode,
            ex.Reason ?? "no reason");

        return StatusCode(ex.StatusCode, new ProblemDetails
        {
            Title = ex.StatusCode == StatusCodes.Status429TooManyRequests
                ? "Availability toggle rate limit exceeded."
                : "Upstream presence service error.",
            Detail = ex.Reason,
            Status = ex.StatusCode
        });
    }

    /// <summary>
    /// Runs a gateway-local in-memory mirror as a BEST-EFFORT side-effect: a
    /// store/downstream blip must NOT 500 a toggle/read whose authoritative
    /// upstream write or read already succeeded. Swallows + logs any exception.
    /// </summary>
    private async Task TryMirrorAsync(Func<Task> mirror, string userId, string label)
    {
        try
        {
            await mirror();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Best-effort availability mirror ({Label}) failed for jeeber {JeeberId}; "
                + "the authoritative upstream operation already succeeded, so the request still returns 200.",
                label,
                userId);
        }
    }

    /// <summary>
    /// Maps the canonical delivery-service presence row onto the public
    /// <see cref="AvailabilityResponse"/> shape. The wire shape is identical to
    /// the pre-S06 in-memory mapping so no existing consumer assertion shifts.
    /// </summary>
    private static AvailabilityResponse ToResponse(JeeberAvailabilityUpstream u, int withdrawnOffers) => new()
    {
        UserId = u.JeeberId,
        Online = u.Online,
        // Offline rows carry no vehicle; preserve the prior "car" default the
        // in-memory mapping emitted (VehicleType defaults to Car) so the
        // required, non-null VehicleType field shape is unchanged.
        VehicleType = string.IsNullOrWhiteSpace(u.VehicleType) ? VehicleType.Car.ToWire() : u.VehicleType!,
        Zone = u.Zone,
        Longitude = u.Lng,
        Latitude = u.Lat,
        LastSeenAt = u.LastSeenAt,
        LastInteractionAt = null,
        UpdatedAt = u.UpdatedAt,
        WithdrawnOffers = withdrawnOffers
    };

    /// <summary>
    /// S06 / ADR-HB-001: maps a heart-beat presence row onto the SAME public
    /// <see cref="AvailabilityResponse"/> shape as the delivery-service mapping.
    /// heart-beat owns ONLY presence (online + recency + lat/lng), so vehicle/zone
    /// are not on its wire — the GET and offline paths emit the prior "car" /
    /// null-zone defaults that the in-memory + delivery offline mappings already
    /// emitted, keeping the required-non-null VehicleType field shape unchanged.
    /// The go-online path uses the <c>vehicleType</c>/<c>zone</c> overload below.
    /// </summary>
    private static AvailabilityResponse ToResponse(HeartBeatPresence p, int withdrawnOffers) => new()
    {
        UserId = p.UserId,
        Online = p.Online,
        VehicleType = VehicleType.Car.ToWire(),
        Zone = null,
        Longitude = p.Lng,
        Latitude = p.Lat,
        LastSeenAt = p.LastSeenAt,
        LastInteractionAt = null,
        UpdatedAt = p.UpdatedAt,
        WithdrawnOffers = withdrawnOffers
    };

    /// <summary>
    /// S06 / ADR-HB-001 go-online overload: maps a heart-beat presence row plus the
    /// Jeeb-semantic <paramref name="vehicleType"/> / <paramref name="zone"/> the
    /// gateway echoes back from the toggle request (heart-beat does not store them)
    /// onto the public response shape — byte-identical to the delivery-service
    /// go-online response.
    /// </summary>
    private static AvailabilityResponse ToResponse(HeartBeatPresence p, string vehicleType, string? zone, int withdrawnOffers) => new()
    {
        UserId = p.UserId,
        Online = p.Online,
        VehicleType = string.IsNullOrWhiteSpace(vehicleType) ? VehicleType.Car.ToWire() : vehicleType,
        Zone = zone,
        Longitude = p.Lng,
        Latitude = p.Lat,
        LastSeenAt = p.LastSeenAt,
        LastInteractionAt = null,
        UpdatedAt = p.UpdatedAt,
        WithdrawnOffers = withdrawnOffers
    };

    /// <summary>
    /// The never-online default returned when delivery-service has no presence
    /// row for the jeeber yet (upstream 404). Offline, no zone/location.
    /// </summary>
    private AvailabilityResponse OfflineDefault(string userId, int withdrawnOffers = 0) => new()
    {
        UserId = userId,
        Online = false,
        VehicleType = VehicleType.Car.ToWire(),
        Zone = null,
        Longitude = null,
        Latitude = null,
        LastSeenAt = null,
        LastInteractionAt = null,
        UpdatedAt = _clock.GetUtcNow(),
        WithdrawnOffers = withdrawnOffers
    };
}
