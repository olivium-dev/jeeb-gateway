using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JeebGateway.Controllers;

/// <summary>
/// Ticket-spec auth surface for T-backend-043: POST /auth/refresh and
/// POST /auth/logout. Both routes delegate to <see cref="ITokenService"/>
/// — the same backing implementation as the legacy /auth/tokens/* routes
/// in <see cref="TokensController"/>, which remain for callers that
/// already speak the longer URL.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("auth")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
// ADR-004 D1: public by design — refresh/logout operate on the rotation cookie, not a bearer.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public: legacy refresh + logout operate on the body refresh-token (not a bearer session),
// so they are anonymous-by-design and bypass L2. (ADR §A notes Logout/Revoke are "any-auth" where they
// require a bearer; this legacy surface authenticates via the body token, hence public — behaviour-preserving.)
[PublicEndpoint("Legacy refresh/logout via body refresh-token (not bearer) — ADR-005 §A public.")]
public class AuthController : ControllerBase
{
    private readonly ITokenService _tokens;

    public AuthController(ITokenService tokens)
    {
        _tokens = tokens;
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(TokenPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "refreshToken is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _tokens.RefreshAsync(body.RefreshToken, ct);
        return result.Outcome switch
        {
            RefreshOutcome.Ok => Ok(ToResponse(result.Tokens!)),
            _ => Unauthorized()
        };
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] RevokeTokenRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "refreshToken is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        await _tokens.RevokeAsync(body.RefreshToken, RevocationReason.Logout, ct);
        return NoContent();
    }

    private static TokenPairResponse ToResponse(TokenPair pair)
    {
        var seconds = (int)Math.Max(
            (pair.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0);
        return new TokenPairResponse
        {
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            TokenType = "Bearer",
            AccessTokenExpiresInSeconds = seconds,
            AccessTokenExpiresAt = pair.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = pair.RefreshTokenExpiresAt
        };
    }
}
