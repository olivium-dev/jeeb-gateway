using JeebGateway.Availability;
using JeebGateway.Matching;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Client-facing matching endpoint (T-backend-008). The Client posts
/// the request id (or a dry-run pickup/tier pair) and the engine returns
/// the count of Jeebers that were notified.
///
/// Two body shapes are accepted:
///
///  - <c>{ requestId }</c> — preferred. The controller resolves pickup
///    and tier from the persisted row, validates ownership (the caller
///    must own the request), and runs matching. The engine pushes a
///    "new offer" to every matched Jeeber.
///
///  - <c>{ pickupLat, pickupLng, tierId }</c> — dry-run shape used by
///    the pre-creation preview UX. No persisted row is required;
///    matching runs against the same online-Jeeber snapshot but never
///    sends pushes (no request id → no idempotency key to dedupe with).
///
/// Either shape may carry an <c>allowedVehicleTypes</c> array; absent
/// or empty means "any vehicle type". Unknown strings in the array
/// reject the request with 400.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("matching")]
public sealed class MatchingController : ControllerBase
{
    private readonly IMatchingService _matching;
    private readonly IRequestsStore _requests;

    public MatchingController(IMatchingService matching, IRequestsStore requests)
    {
        _matching = matching;
        _requests = requests;
    }

    [HttpPost("run")]
    [RequireRole(Roles.Client)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(MatchingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Run([FromBody] MatchingRunRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;
        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Resolve allowed vehicle types — empty / null means "any". Unknown
        // strings reject early so the matching engine never sees junk.
        var allowed = new HashSet<VehicleType>();
        if (body.AllowedVehicleTypes is { Count: > 0 } raw)
        {
            foreach (var entry in raw)
            {
                if (!VehicleTypeExtensions.TryParseWire(entry, out var v))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unknown vehicle type in allowedVehicleTypes.",
                        Detail = $"received='{entry}'. Allowed: car, motorbike, bicycle, scooter, walk.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/vehicle-type-invalid"
                    });
                }
                allowed.Add(v);
            }
        }

        string requestId;
        double pickupLat;
        double pickupLng;
        string tierId;

        if (!string.IsNullOrWhiteSpace(body.RequestId))
        {
            var existing = await _requests.GetAsync(body.RequestId, ct);
            if (existing is null) return NotFound();
            if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "Only the owning Client may run matching for this request.",
                    Status = StatusCodes.Status403Forbidden
                });
            }
            if (existing.PickupLocation is null || string.IsNullOrEmpty(existing.TierId))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Request is missing pickup location or tier — cannot run matching.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            requestId = existing.Id;
            pickupLat = existing.PickupLocation.Lat;
            pickupLng = existing.PickupLocation.Lng;
            tierId = existing.TierId;
        }
        else
        {
            if (body.PickupLat is null || body.PickupLng is null || string.IsNullOrWhiteSpace(body.TierId))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Either requestId or (pickupLat + pickupLng + tierId) must be supplied.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/matching-input"
                });
            }
            // WGS84 sanity — keep the engine on real Earth coordinates.
            if (body.PickupLat is < -90 or > 90
                || body.PickupLng is < -180 or > 180
                || double.IsNaN(body.PickupLat.Value) || double.IsNaN(body.PickupLng.Value))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "pickupLat / pickupLng must be valid WGS84 coordinates.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/location-invalid"
                });
            }

            // Dry-run id — never sent to the push pipeline because dry-run
            // bodies don't carry a real request id. Used only for logging.
            requestId = $"dryrun:{Guid.NewGuid()}";
            pickupLat = body.PickupLat.Value;
            pickupLng = body.PickupLng.Value;
            tierId = body.TierId!;
        }

        MatchingOutcome outcome;
        try
        {
            outcome = await _matching.RunAsync(new MatchingInput
            {
                RequestId = requestId,
                PickupLat = pickupLat,
                PickupLng = pickupLng,
                TierId = tierId,
                AllowedVehicleTypes = allowed
            }, ct);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("Unknown tier", StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "tierId does not match any active delivery tier.",
                Detail = $"tierId={tierId}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/tier-not-found"
            });
        }

        return Ok(new MatchingRunResponse
        {
            RequestId = outcome.RequestId,
            TierId = outcome.TierId,
            RadiusKm = outcome.RadiusKm,
            NotifiedCount = outcome.NotifiedCount,
            CandidateCount = outcome.Candidates.Count,
            Candidates = outcome.Candidates
                .Select(c => new MatchedJeeberDto
                {
                    UserId = c.UserId,
                    VehicleType = c.VehicleType.ToWire(),
                    DistanceKm = c.DistanceKm,
                    Rating = c.Rating
                })
                .ToList(),
            ElapsedMs = outcome.ElapsedMs
        });
    }
}
