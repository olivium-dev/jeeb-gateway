using JeebGateway.Matching;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over matching-service (Python FastAPI) Jeeb route
/// (app/api/endpoints/jeeb_matching.py). Used by
/// <see cref="JeebGateway.Controllers.MatchingController"/> when
/// <c>FeatureFlags:UseUpstream:Matching</c> is set.
/// </summary>
public interface IMatchingServiceClient
{
    Task<MatchingRunResponse> RunMatchingAsync(MatchingRunRequest body, CancellationToken ct);
}
