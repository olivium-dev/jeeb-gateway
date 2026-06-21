using JeebGateway.Auth.Capabilities;
using JeebGateway.Matching;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// iter6 B8 — client-facing matching BFF slice. The mobile waiting screen
/// (JM-025/JM-026, <c>DioOrderBroadcastService</c>) flips a freshly-created
/// request "live" by calling two gateway paths that previously 404'd, leaving
/// the request stuck pending → expired in the client UI:
///
/// <list type="bullet">
///   <item><c>POST /v1/matching/find-jeebers</c> — coverage probe. Returns the
///     count of nearby online Jeebers (no fan-out). Best-effort: a matching
///     hiccup yields count 0 so the screen shows its no-coverage variant.</item>
///   <item><c>POST /v1/matching/broadcast</c> — fan the request out to matched
///     Jeebers (202). This is the authoritative "request is now live" signal;
///     it runs the same tier-aware match with <c>broadcast=true</c> so the
///     matching-service publishes the match event to the tier topic.</item>
/// </list>
///
/// Thin BFF over matching-service <c>POST /api/v1/matching/find-jeebers</c>
/// (FastAPI, Services:Matching:BaseUrl). Tier + origin are resolved from the
/// persisted request row when the body omits them (the create-leg already
/// pinned them); the body may also carry them directly for the dry-run shape.
/// Coexists with the legacy (Obsolete) <see cref="JeebGateway.Controllers.MatchingController"/>
/// /matching/run surface — all new matching work lands on the v1 paths here.
/// </summary>
[ApiController]
public sealed class JeebMatchingController : ControllerBase
{
    // Matching-service requires a tier identifier; when neither the request row
    // nor the body provides one, fall back to a reachable tier so the coverage
    // probe still 200s rather than 422 (mirrors the mobile client default).
    private const string DefaultTier = "express";

    private readonly IMatchingServiceClient _matching;
    private readonly IRequestsStore _requests;
    private readonly ILogger<JeebMatchingController> _logger;

    public JeebMatchingController(
        IMatchingServiceClient matching,
        IRequestsStore requests,
        ILogger<JeebMatchingController> logger)
    {
        _matching = matching;
        _requests = requests;
        _logger = logger;
    }

    /// <summary>
    /// Coverage probe: how many online Jeebers are near the request, by tier.
    /// No fan-out (broadcast=false). Best-effort — never the hard-fail leg.
    /// </summary>
    [HttpPost("v1/matching/find-jeebers")]
    [RequireCapability(Capabilities.MatchingRun)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(FindJeebersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> FindJeebers(
        [FromBody] MatchingBffRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
        {
            return problem;
        }

        var resolved = await ResolveAsync(body, clientId, ct);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        // Coverage-only: do NOT broadcast here. A matching failure is non-fatal
        // (the mobile client treats find-jeebers as best-effort) — return count 0
        // so the waiting screen renders its no-coverage variant instead of 500ing.
        try
        {
            var upstream = await _matching.FindJeebersAsync(
                new MatchingFindJeebersUpstreamRequest
                {
                    RequestId = resolved.RequestId,
                    TierName = resolved.Tier,
                    OriginLatitude = resolved.Lat,
                    OriginLongitude = resolved.Lng,
                    Broadcast = false
                }, ct);

            return Ok(new FindJeebersResponse
            {
                Count = upstream.MatchCount,
                JeeberIds = upstream.JeeberIds,
                RequestId = upstream.RequestId ?? resolved.RequestId,
                Tier = upstream.TierName ?? resolved.Tier,
                RadiusKm = upstream.RadiusKm,
                BroadcastPublished = upstream.BroadcastPublished
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "B8: matching find-jeebers failed for request {RequestId}; returning 0-coverage",
                resolved.RequestId);
            return Ok(new FindJeebersResponse
            {
                Count = 0,
                JeeberIds = Array.Empty<string>(),
                RequestId = resolved.RequestId,
                Tier = resolved.Tier,
                RadiusKm = 0,
                BroadcastPublished = false
            });
        }
    }

    /// <summary>
    /// Fan the request out to matched Jeebers (202). Runs the tier-aware match
    /// with broadcast=true so matching-service publishes to the tier topic.
    /// This is the authoritative "request is now live" signal for the client.
    /// </summary>
    [HttpPost("v1/matching/broadcast")]
    [RequireCapability(Capabilities.MatchingRun)]
    [RequireActiveUser]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Broadcast(
        [FromBody] MatchingBffRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
        {
            return problem;
        }

        var resolved = await ResolveAsync(body, clientId, ct);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        // Fire-and-forget by contract: the request is already persisted/pending,
        // so the broadcast is the "go live" trigger. A matching-service hiccup
        // must NOT block the client — log + still return 202 (the request stays
        // pending and the client polls/retries), matching the mobile expectation
        // that a non-2xx here is the only thing that would abort going live.
        try
        {
            await _matching.FindJeebersAsync(
                new MatchingFindJeebersUpstreamRequest
                {
                    RequestId = resolved.RequestId,
                    TierName = resolved.Tier,
                    OriginLatitude = resolved.Lat,
                    OriginLongitude = resolved.Lng,
                    Broadcast = true
                }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "B8: matching broadcast failed for request {RequestId}; request stays pending",
                resolved.RequestId);
        }

        return Accepted();
    }

    /// <summary>
    /// Resolves tier + origin for the matching call. Prefers the body-supplied
    /// values (dry-run / preview shape); falls back to the persisted request row
    /// (the create-leg pinned tier + pickup). Enforces request ownership when a
    /// row is found so a client cannot broadcast another client's request.
    /// </summary>
    private async Task<ResolvedMatch> ResolveAsync(
        MatchingBffRequest? body, string clientId, CancellationToken ct)
    {
        var requestId = body?.RequestId;
        var tier = body?.Tier;
        double? lat = body?.Origin?.Lat;
        double? lng = body?.Origin?.Lng;

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            var row = await _requests.GetAsync(requestId, ct);
            if (row is not null)
            {
                // Ownership: only the request owner may run matching for it.
                if (!string.Equals(row.ClientId, clientId, StringComparison.Ordinal))
                {
                    return ResolvedMatch.Fail(new ForbidResult());
                }
                tier ??= row.TierId;
                if (lat is null || lng is null)
                {
                    lat ??= row.PickupLocation?.Lat;
                    lng ??= row.PickupLocation?.Lng;
                }
            }
        }

        return new ResolvedMatch
        {
            RequestId = requestId,
            Tier = string.IsNullOrWhiteSpace(tier) ? DefaultTier : tier,
            Lat = lat ?? 0,
            Lng = lng ?? 0
        };
    }

    private sealed class ResolvedMatch
    {
        public string? RequestId { get; init; }
        public string Tier { get; init; } = DefaultTier;
        public double Lat { get; init; }
        public double Lng { get; init; }
        public IActionResult? Problem { get; init; }

        public static ResolvedMatch Fail(IActionResult problem) =>
            new() { Problem = problem };
    }
}

/// <summary>
/// Mobile-contract body for the v1 matching BFF paths
/// (<c>DioOrderBroadcastService</c>): a request id plus optional tier + origin
/// override. Tier/origin are resolved from the request row when omitted.
/// </summary>
public sealed class MatchingBffRequest
{
    public string? RequestId { get; set; }
    public string? Tier { get; set; }
    public MatchingOrigin? Origin { get; set; }
}

/// <summary>Origin point (mobile sends <c>{lat,lng}</c>).</summary>
public sealed class MatchingOrigin
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}
