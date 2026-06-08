using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

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
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("jeebers/me/availability")]
// ADR-005 L2 §D jeeber-only: class-level (both read + toggle of own availability are jeeber-typed).
// Replaces class-level [RequireRole(Roles.Jeeber)].
[RequireCapability(Capabilities.AvailabilityToggle)]
public class AvailabilityController : ControllerBase
{
    private readonly IAvailabilityStore _store;
    private readonly IDeliveryServiceClient _delivery;
    private readonly TimeProvider _clock;

    public AvailabilityController(
        IAvailabilityStore store,
        IDeliveryServiceClient delivery,
        TimeProvider clock)
    {
        _store = store;
        _delivery = delivery;
        _clock = clock;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        // Read presence from the canonical delivery-service store. A never-online
        // jeeber (upstream 404 → null) yields an offline default — never a 500.
        var upstream = await _delivery.GetAvailabilityAsync(userId, ct);

        // Mirror the interaction watermark into the in-memory store so the
        // gateway-local auto-offline sweeper (no upstream equivalent yet) still
        // sees this read as activity.
        await _store.RecordInteractionAsync(userId, _clock.GetUtcNow(), ct);

        return Ok(upstream is null
            ? OfflineDefault(userId)
            : ToResponse(upstream, withdrawnOffers: 0));
    }

    [HttpPatch]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Patch([FromBody] AvailabilityPatchRequest? body, CancellationToken ct)
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

            // S06 keystone: write presence to the canonical delivery-service store
            // FIRST so the matching run sees this jeeber as a candidate.
            var upstream = await _delivery.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
            {
                Online = true,
                VehicleType = vehicle.ToWire(),
                Zone = zone,
                Lat = body.Latitude,
                Lng = body.Longitude
            }, userId, ct);

            // Mirror into the gateway-local store that backs the admin ops-map +
            // auto-offline sweeper (no upstream read equivalent yet).
            await _store.GoOnlineAsync(userId, new GoOnlineRequest
            {
                VehicleType = vehicle,
                Zone = zone,
                Longitude = body.Longitude,
                Latitude = body.Latitude
            }, ct);

            return Ok(ToResponse(upstream, withdrawnOffers: 0));
        }

        // Offline path. Write the offline transition upstream FIRST (this is the
        // N13 fix: the offline path no longer depends on the in-memory
        // GoOfflineAsync as its primary writer). The upstream offline shape does
        // not carry vehicle/zone — they are cleared.
        var offlineUpstream = await _delivery.SetAvailabilityAsync(new JeeberAvailabilityUpstreamRequest
        {
            Online = false
        }, userId, ct);

        // Mirror offline into the gateway-local store so the admin ops-map drops
        // the jeeber and any in-flight offers are withdrawn (offer accounting has
        // no upstream read route yet). The withdrawn-offer count comes from this
        // local accounting, which S06 does not assert on the offline response.
        var offlineLocal = await _store.GoOfflineAsync(userId, GoOfflineReason.UserToggle, ct);

        return Ok(ToResponse(offlineUpstream, offlineLocal.WithdrawnOffers));
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
    /// The never-online default returned when delivery-service has no presence
    /// row for the jeeber yet (upstream 404). Offline, no zone/location.
    /// </summary>
    private AvailabilityResponse OfflineDefault(string userId) => new()
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
        WithdrawnOffers = 0
    };
}
