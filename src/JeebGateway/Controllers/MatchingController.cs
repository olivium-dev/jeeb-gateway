using JeebGateway.Auth.Capabilities;
using JeebGateway.Matching;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace JeebGateway.Controllers;

/// <summary>
/// Client-facing matching endpoint (T-backend-008). The Client posts the
/// request id (or a dry-run pickup/tier pair); the response carries the count
/// of Jeebers that were notified plus the ordered candidate list.
///
/// Courier matching has been relocated out of the gateway into delivery-service
/// (DELIVERY-SERVICE-RELOCATION-DESIGN.md §2.1). The gateway no longer owns a
/// matching engine — this controller is a thin BFF that forwards the request to
/// delivery-service <c>POST /api/v1/matching/run</c> and surfaces the result.
///
/// Two body shapes are accepted and validated <b>by delivery-service</b>:
///
///  - <c>{ requestId }</c> — preferred. delivery-service resolves pickup + tier
///    from the persisted row, authorizes the caller, runs matching, and pushes a
///    "new offer" to every matched Jeeber.
///
///  - <c>{ pickupLat, pickupLng, tierId }</c> — dry-run preview shape. No
///    persisted row required; matching runs against the same online-Jeeber
///    snapshot but never sends pushes.
///
/// Either shape may carry an <c>allowedVehicleTypes</c> array; absent or empty
/// means "any vehicle type". Input validation (unknown vehicle type, missing
/// pickup+tier in preview mode, unknown tier/request, non-positive radius) is
/// owned by delivery-service, which returns 400/404/422; the controller maps
/// those straight through as RFC 7807 ProblemDetails.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("matching")]
public sealed class MatchingController : ControllerBase
{
    // delivery-service requires a tenant scope on /matching/run. The gateway is
    // single-tenant today; the value is config-overridable so a future
    // multi-tenant rollout does not need a code change. Default matches the
    // delivery-service DB default ('default' — design §2.1 / schema §3).
    private const string DefaultTenantId = "default";

    private readonly IDeliveryServiceClient _delivery;
    private readonly IMatchingServiceClient _matchesRead;
    private readonly IDeliveryRowMirror _rowMirror;
    private readonly string _tenantId;

    public MatchingController(
        IDeliveryServiceClient delivery,
        IMatchingServiceClient matchesRead,
        IDeliveryRowMirror rowMirror,
        IConfiguration config)
    {
        _delivery = delivery;
        _matchesRead = matchesRead;
        _rowMirror = rowMirror;
        _tenantId = config["Services:Delivery:TenantId"] ?? DefaultTenantId;
    }

    [HttpPost("run")]
    // ADR-005 L2 §C client-only: replaces [RequireRole(Roles.Client)].
    [RequireCapability(Capabilities.MatchingRun)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(MatchingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Run([FromBody] MatchingRunRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var problem)) return problem;
        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // S06 just-in-time mirror: in request_id mode the request row may live
        // ONLY in the gateway's in-memory store (the create-time durable mirror in
        // DurableRequestsStore is gated behind the heavier FeatureFlags:Durable
        // Requests switch). Before forwarding, best-effort seed the canonical
        // delivery-service deliveries row so POST /api/v1/matching/run resolves it
        // instead of returning 404 unknown_request_id. This is thin BFF
        // orchestration: the gateway composes two existing delivery-service typed
        // -client calls (idempotent seed-row → run) — it does NOT couple two
        // microservices and it holds no matching/delivery domain state. The seed is
        // BEST-EFFORT and NEVER throws: the dry-run/preview shape (no requestId),
        // an unknown id, or a seed hiccup all fall through to delivery-service,
        // which stays the canonical authority for the run outcome (including a
        // genuine 404). Skipped entirely when MatchingMirror.Enabled is false.
        if (!string.IsNullOrWhiteSpace(body.RequestId))
        {
            await _rowMirror.EnsureSeededAsync(body.RequestId, ct);
        }

        // Thin BFF: forward verbatim to delivery-service, which owns input
        // validation, the dry-run branch, request-ownership authz, and the push
        // fan-out. Map the snake_case Go result onto the gateway DTO.
        DeliveryMatchingRunResult result;
        try
        {
            result = await _delivery.RunMatchingAsync(new DeliveryMatchingRunRequest
            {
                RequestId = body.RequestId,
                PickupLat = body.PickupLat,
                PickupLng = body.PickupLng,
                TierId = body.TierId,
                AllowedVehicleTypes = body.AllowedVehicleTypes,
                TenantId = _tenantId
            }, ct);
        }
        catch (DeliveryMatchingException ex)
        {
            // delivery-service owns the matching contract; the gateway surfaces
            // its 400/404/422 straight through as RFC 7807 rather than
            // re-interpreting the failure.
            return StatusCode(ex.StatusCode, new ProblemDetails
            {
                Title = "delivery-service rejected the matching request.",
                Detail = ex.Reason,
                Status = ex.StatusCode,
                Type = "https://jeeb.dev/errors/matching-rejected"
            });
        }

        return Ok(new MatchingRunResponse
        {
            RequestId = result.RequestId,
            // Client-facing $.tierId is the lowercase tier CODE
            // (flash/standard/express) — the human-readable tier the client
            // ordered — sourced from delivery-service's tier_code. Fall back to
            // the tier UUID only if an older delivery-service build omits the
            // code, so the field is never null/empty.
            TierId = string.IsNullOrWhiteSpace(result.TierCode) ? result.TierId : result.TierCode!,
            RadiusKm = result.RadiusKm,
            NotifiedCount = result.NotifiedCount,
            CandidateCount = result.CandidateCount,
            Candidates = result.Candidates
                .Select(c => new MatchedJeeberDto
                {
                    UserId = c.UserId,
                    VehicleType = c.VehicleType,
                    DistanceKm = c.DistanceKm,
                    Rating = c.Rating
                })
                .ToList(),
            ElapsedMs = result.ElapsedMs
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
    // ADR-005 L2: this read is "authenticated, ANY role" (no user-type restriction) per its
    // contract. There is no narrower user-type gate here, so it is an explicit L2 public opt-out
    // (L1 [Authorize] is preserved above — this is NOT [AllowAnonymous]). Marking it preserves the
    // any-authenticated behaviour exactly and satisfies the default-deny coverage guard.
    [PublicEndpoint("Authenticated any-role match-candidate read; no L2 user-type gate (L1 auth preserved).")]
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
            upstream = await _matchesRead.GetMatchesAsync(userId, skip, limit, ct);
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
