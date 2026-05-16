using JeebGateway.Availability;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("jeebers/me/availability")]
[RequireRole(Roles.Jeeber)]
public class AvailabilityController : ControllerBase
{
    private readonly IAvailabilityStore _store;
    private readonly TimeProvider _clock;

    public AvailabilityController(IAvailabilityStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        await _store.RecordInteractionAsync(userId, _clock.GetUtcNow(), ct);
        var availability = await _store.GetAsync(userId, ct);
        return Ok(ToResponse(availability, withdrawnOffers: 0));
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

            var result = await _store.GoOnlineAsync(userId, new GoOnlineRequest
            {
                VehicleType = vehicle,
                Zone = body.Zone!.Trim(),
                Longitude = body.Longitude,
                Latitude = body.Latitude
            }, ct);

            return Ok(ToResponse(result.Availability, withdrawnOffers: 0));
        }

        var offline = await _store.GoOfflineAsync(userId, GoOfflineReason.UserToggle, ct);
        return Ok(ToResponse(offline.Availability, offline.WithdrawnOffers));
    }

    private static AvailabilityResponse ToResponse(JeeberAvailability r, int withdrawnOffers) => new()
    {
        UserId = r.UserId,
        Online = r.IsOnline,
        VehicleType = r.VehicleType.ToWire(),
        Zone = r.Zone,
        Longitude = r.Longitude,
        Latitude = r.Latitude,
        LastSeenAt = r.LastSeenAt,
        LastInteractionAt = r.LastInteractionAt,
        UpdatedAt = r.UpdatedAt,
        WithdrawnOffers = withdrawnOffers
    };
}
