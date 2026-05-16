using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JeebGateway.Controllers;

/// <summary>
/// JWT rotation endpoints (T-backend-043).
///
/// Issue:    POST /auth/tokens          — called by auth-service after OTP verification.
/// Refresh:  POST /auth/tokens/refresh  — single-use; rotates and returns a new pair.
/// Revoke:   POST /auth/tokens/revoke   — logout for one device.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("auth/tokens")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
public class TokensController : ControllerBase
{
    private readonly ITokenService _tokens;
    private readonly IUsersStore _users;

    public TokensController(ITokenService tokens, IUsersStore users)
    {
        _tokens = tokens;
        _users = users;
    }

    [HttpPost]
    [ProducesResponseType(typeof(TokenPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Issue([FromBody] IssueTokensRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "userId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Make sure a profile shell exists so subsequent /users/me works.
        var profile = await _users.GetOrCreateAsync(body.UserId, ct);
        var roles = body.Roles is { Count: > 0 } ? body.Roles : profile.Roles;

        var pair = await _tokens.IssueAsync(body.UserId, roles, ct);
        return Ok(ToResponse(pair));
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
            RefreshOutcome.NotFound => Unauthorized(),
            RefreshOutcome.Expired => Unauthorized(),
            RefreshOutcome.Revoked => Unauthorized(),
            RefreshOutcome.ReuseDetected => Unauthorized(),
            _ => Unauthorized()
        };
    }

    [HttpPost("revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Revoke([FromBody] RevokeTokenRequest? body, CancellationToken ct)
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
