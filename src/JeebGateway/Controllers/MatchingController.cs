using JeebGateway.Availability;
using JeebGateway.Matching;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly IMatchingServiceClient _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public MatchingController(
        IMatchingService matching,
        IRequestsStore requests,
        IMatchingServiceClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _matching = matching;
        _requests = requests;
        _upstream = upstream;
        _flags = flags;
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

        // T-migrate-gateway-proxies (PR-A): proxy to upstream matching-service
        // when the flag is on. Input validation and the dry-run branch live
        // upstream, so we forward the body verbatim and surface the response.
        if (_flags.CurrentValue.Matching)
        {
            var upstreamResponse = await _upstream.RunMatchingAsync(body, ct);
            return Ok(upstreamResponse);
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

    /// <summary>
    /// Returns paginated match candidates for the given user, sourced
    /// directly from the matching-service Postgres DB.
    ///
    /// Gateway route : GET /matching/users/{userId}?skip=0&amp;limit=10
    /// Upstream route: GET /api/v1/matches/{user_id}?skip=…&amp;limit=…
    ///   (matching/app/api/endpoints/matches.py)
    ///
    /// The caller must be authenticated (any role). The upstream 404 surface
    /// when no preference rows exist for the user is forwarded as 404 + ProblemDetails.
    /// </summary>
    [HttpGet("users/{userId}")]
    [Authorize]
    [ProducesResponseType(typeof(MatchingUsersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetMatchingUsers(
        string userId,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "userId is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/missing-user-id"
            });
        }

        if (skip < 0 || limit < 1 || limit > 500)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "skip must be >= 0 and limit must be between 1 and 500.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-pagination"
            });
        }

        MatchingServiceMatchesResponse upstream;
        try
        {
            upstream = await _upstream.GetMatchesAsync(userId, skip, limit, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No matching preferences found for user.",
                Detail = $"userId={userId}. Ensure the user has completed preference setup in matching-service.",
                Status = StatusCodes.Status404NotFound,
                Type = "https://jeeb.dev/errors/matching-preferences-not-found"
            });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "matching-service is unavailable.",
                Detail = ex.Message,
                Status = StatusCodes.Status502BadGateway,
                Type = "https://jeeb.dev/errors/upstream-unavailable"
            });
        }

        return Ok(new MatchingUsersResponse
        {
            UserId = userId,
            Matches = upstream.Matches,
            Total = upstream.Total,
            Skip = skip,
            Limit = limit
        });
    }
}
