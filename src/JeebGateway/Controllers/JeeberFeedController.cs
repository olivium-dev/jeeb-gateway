using JeebGateway.Auth.Capabilities;
using JeebGateway.Matching;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeber-facing pull feed — the inverse of <see cref="MatchingController"/>'s
/// <c>/matching/run</c>. Where the matching run answers "given a delivery, which
/// jeebers should be notified?", the feed answers "given THIS jeeber, which open
/// deliveries should they see right now?":
///
///   <c>GET /jeebers/me/feed?limit=N</c>
///
/// <para>
/// Thin BFF (ADR-0001 / ADR-0005): the gateway holds NO feed state. It resolves
/// the authenticated caller, forwards to the canonical delivery-service route
/// <c>GET /api/v1/jeebers/{id}/feed</c> via <see cref="IDeliveryServiceClient"/>,
/// and maps the snake_case Go result onto the public camelCase shape.
/// delivery-service owns the GPS resolution, the per-delivery tier-radius cut,
/// the active-restriction filter, the ordering, and the page cap — exactly the
/// same collaborators the matching run uses, inverted.
/// </para>
/// <para>
/// Gated <see cref="Capabilities.AvailabilityToggle"/> (jeeber-only — the same
/// jeeber-typed capability that fronts the availability toggle, since the feed is
/// a jeeber's view of work they can take) + <see cref="RequireActiveUserAttribute"/>
/// (only an active jeeber may pull work). A never-online jeeber (upstream 404)
/// yields an empty feed, never a 500.
/// </para>
/// </summary>
[ApiController]
[Route("jeebers/me/feed")]
// ADR-005 L2 §D jeeber-only: the feed is a jeeber's view of claimable work.
[RequireCapability(Capabilities.AvailabilityToggle)]
[RequireActiveUser]
public sealed class JeeberFeedController : ControllerBase
{
    /// <summary>Upper bound forwarded to delivery-service; the service clamps to
    /// its own max (100). Mirrors the upstream default of 20 when omitted.</summary>
    private const int MaxLimit = 100;

    private readonly IDeliveryServiceClient _delivery;

    public JeeberFeedController(IDeliveryServiceClient delivery)
    {
        _delivery = delivery;
    }

    [HttpGet]
    [ProducesResponseType(typeof(JeeberFeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get([FromQuery] int? limit, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        // A non-positive limit is a malformed page request → 400 (a 0/-N page is
        // never meaningful). An over-max value is clamped to MaxLimit rather than
        // rejected so a generous client still gets a bounded page. Null omits the
        // parameter and delivery-service applies its default (20).
        int? effectiveLimit = limit;
        if (limit is int n)
        {
            if (n <= 0)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Query 'limit' must be a positive integer.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/invalid-pagination"
                });
            }
            effectiveLimit = Math.Min(n, MaxLimit);
        }

        // Thin BFF passthrough: delivery-service owns the feed pipeline. A
        // never-online jeeber (upstream 404) comes back as an empty feed from the
        // client, so the controller never has to special-case it.
        var result = await _delivery.GetJeeberFeedAsync(userId, effectiveLimit, ct);

        return Ok(new JeeberFeedResponse
        {
            JeeberId = string.IsNullOrWhiteSpace(result.JeeberId) ? userId : result.JeeberId!,
            Items = result.Items
                .Select(i => new JeeberFeedItemDto
                {
                    RequestId = i.RequestId,
                    // Surface the lowercase tier CODE (flash/standard/express) — the
                    // human-readable tier — falling back to the tier UUID only if an
                    // older delivery-service build omits the code, so it is never empty.
                    TierId = string.IsNullOrWhiteSpace(i.TierCode) ? (i.TierId ?? string.Empty) : i.TierCode!,
                    PickupLat = i.PickupLat,
                    PickupLng = i.PickupLng,
                    DistanceKm = i.DistanceKm,
                    CreatedAt = i.CreatedAt
                })
                .ToList(),
            Count = result.Count
        });
    }
}
