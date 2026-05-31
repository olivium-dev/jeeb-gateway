using JeebGateway.Matching;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over matching-service (Python FastAPI).
/// <list type="bullet">
/// <item><see cref="RunMatchingAsync"/> — legacy POST route (path 404s; kept for backward compat).</item>
/// <item><see cref="GetMatchesAsync"/> — real DB-backed route:
///   <c>GET /api/v1/matches/{userId}?skip={skip}&amp;limit={limit}</c>
///   (app/api/endpoints/matches.py).</item>
/// </list>
/// Used by <see cref="JeebGateway.Controllers.MatchingController"/>.
/// </summary>
public interface IMatchingServiceClient
{
    Task<MatchingRunResponse> RunMatchingAsync(MatchingRunRequest body, CancellationToken ct);

    /// <summary>
    /// Calls the real matching-service DB read:
    /// <c>GET /api/v1/matches/{userId}?skip=…&amp;limit=…</c>
    /// Returns the paginated list of matched user IDs and total count.
    /// </summary>
    Task<MatchingServiceMatchesResponse> GetMatchesAsync(
        string userId, int skip, int limit, CancellationToken ct);
}
