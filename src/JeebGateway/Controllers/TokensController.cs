using System.Security.Cryptography;
using System.Text;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Push;
using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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
// ADR-004 D1: public by design — the owner-decided mint backdoor; gated internally by
// the X-Service-Auth-Key check (AuthorizeMint), not by the bearer audience policy.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public: token mint (incl. super-login, gated by X-Service-Auth-Key), refresh, revoke.
// Bypasses L2 — the X-Service-Auth-Key IS the privileged gate (owner decision #2 super-login seam).
[PublicEndpoint("Token mint/refresh/revoke incl. super-login (X-Service-Auth-Key gated) — ADR-005 §A.")]
public class TokensController : ControllerBase
{
    private readonly ITokenService _tokens;
    private readonly IUsersStore _users;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly IOptionsMonitor<JwtOptions> _jwt;
    private readonly IOptionsMonitor<NotificationTestSeamOptions> _seam;
    private readonly ILogger<TokensController> _logger;

    public TokensController(
        ITokenService tokens,
        IUsersStore users,
        IOptionsMonitor<SecurityOptions> security,
        IOptionsMonitor<JwtOptions> jwt,
        IOptionsMonitor<NotificationTestSeamOptions> seam,
        ILogger<TokensController> logger)
    {
        _tokens = tokens;
        _users = users;
        _security = security;
        _jwt = jwt;
        _seam = seam;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(TokenPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Issue([FromBody] IssueTokensRequest? body, CancellationToken ct)
    {
        // F3 security-P0 — privileged-caller gate. The credential-less mint was an
        // account-takeover backdoor: any caller could mint a gateway JWT for an
        // arbitrary userId. The mint now requires a privileged shared key so only
        // an authorized internal caller (the test harness / CTO / a real auth flow)
        // can issue tokens. Evaluated BEFORE any work (incl. profile creation) so
        // an unauthenticated caller cannot probe or mutate state.
        var gate = AuthorizeMint();
        if (gate is not null)
        {
            return gate;
        }

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

        // N2 PER-REQUEST role-less mint (additive, NO global flag, test-only opt-in).
        //
        // The negative case S12.N2 needs a genuinely role-less, active_role-less
        // token so the shared ADR-005 Layer-2 capability guard rejects the role-less
        // caller (403) BEFORE the device-register action runs. Owner decision
        // (cto-s12, ADR-005): a role-less authenticated caller's correct status is
        // 403 (capability), NOT 400.
        //
        // The opt-in is the SHAPE OF THE REQUEST ITSELF: an EXPLICIT empty roles
        // array (roles: []) is treated as "mint with no roles". This is distinct
        // from an ABSENT/null roles field, which keeps the historical
        // profile-default fallback untouched. The console mint proxy forwards an
        // explicit roles:[] faithfully (an empty array is truthy in JS at
        // server/api/run.post.ts), so NO console-proxy edit and NO :3040 restart is
        // needed.
        //
        // Why per-request and not the global seam flag: the prior global flag
        // (NotificationTestSeam:HonorExplicitEmptyRoles) collapsed null OR empty
        // into role-less intent, which broke S01 — S01 mints with an ABSENT roles
        // field and MUST fall back to its profile default. Distinguishing
        // explicit-empty (this request) from absent (S01) makes the opt-in
        // self-scoping: ONLY a caller that literally sends roles:[] (sole occurrence
        // fleet-wide = S12 SETUP-4) gets a role-less token. The global flag stays
        // OFF; this path does not read it, so S01's absent-roles mint is unaffected.
        var explicitEmptyRoles = body.Roles is { Count: 0 };
        if (explicitEmptyRoles)
        {
            // Persist a role-less projection so IssueAsync reads active_role="" too.
            await _users.UpsertProjectionAsync(new UserProfile
            {
                Id = profile.Id,
                Phone = profile.Phone,
                Name = profile.Name,
                Roles = new List<string>(),
                ActiveRole = string.Empty,
                CreatedAt = profile.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);

            var roleless = await _tokens.IssueAsync(body.UserId, Array.Empty<string>(), ct);
            _logger.LogInformation(
                "auth.tokens N2 per-request: minted role-less token for userId={UserId} (explicit roles:[])", body.UserId);
            return Ok(ToResponse(roleless));
        }

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

    /// <summary>
    /// Enforces the F3 privileged-caller gate on the token mint.
    /// Returns <c>null</c> when the caller is authorized; otherwise returns the
    /// <see cref="IActionResult"/> to short-circuit with (401 = no key,
    /// 403 = wrong key). Uses constant-time comparison to avoid a timing
    /// side-channel on the key.
    /// </summary>
    private IActionResult? AuthorizeMint()
    {
        var cfg = _security.CurrentValue.TokenMint;
        if (!cfg.Enabled)
        {
            // Gate disabled (tests / local dev) — mint is open by configuration.
            return null;
        }

        // Fall back to the JWT signing key when no dedicated mint key is set, so
        // the gate is never accidentally left open by a missing pipeline secret.
        var expected = string.IsNullOrWhiteSpace(cfg.Key)
            ? _jwt.CurrentValue.SigningKey
            : cfg.Key;

        if (string.IsNullOrWhiteSpace(expected))
        {
            // No credential is derivable at all — fail closed rather than open.
            _logger.LogError(
                "Token mint gate enabled but no key is configured and the JWT signing key is empty; refusing to mint.");
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Token minting is not available.",
                Detail = "The token mint is gated but no privileged key is configured.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (!Request.Headers.TryGetValue(cfg.HeaderName, out var provided)
            || string.IsNullOrWhiteSpace(provided))
        {
            _logger.LogWarning(
                "Credential-less call to POST /auth/tokens rejected (missing {Header}).", cfg.HeaderName);
            return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = $"A privileged service credential is required in the {cfg.HeaderName} header to mint a token.",
                Status = StatusCodes.Status401Unauthorized
            });
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            _logger.LogWarning("POST /auth/tokens called with an invalid privileged key.");
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden",
                Detail = "The provided service credential is not valid.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        return null;
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
