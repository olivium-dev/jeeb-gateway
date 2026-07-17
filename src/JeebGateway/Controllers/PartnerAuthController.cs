using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Partner.Auth;
using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeb Partner Portal login front door (PP-1, partner-wallet-bff): <c>POST /v1/partner/auth/login</c>.
///
/// <para><b>The one endpoint that was missing.</b> The portal already POSTs
/// <c>{ identifier, password }</c> here and reads <c>token</c> from the response; before PP-1 the
/// gateway had no such route and the call fell into the void. This controller closes that seam.</para>
///
/// <para><b>Mint mirrors the customer paths exactly.</b> On a verified credential it mints the SAME
/// kind of gateway session as OTP-verify and super-login: <see cref="ITokenService.IssueAsync"/> signs
/// an access JWT with <c>iss=jeeb-gateway</c> / <c>aud=jeeb-clients</c> (owner decision — NOT a
/// user-management-audience token) carrying the <c>partner</c> role claim (<see cref="Roles.Partner"/>,
/// from the authoritative role list). That token passes Layer 1 (gateway audience) and Layer 2 (the
/// ADR-005 <c>partner.*</c> capability gate) on every partner-wallet route — no bespoke auth.</para>
///
/// <para><b>Public front door (ADR-005 §A).</b> Marked <see cref="PublicEndpointAttribute"/> because it
/// PRECEDES the session token (the mint IS the token) — like OTP request/verify and the token mint.
/// Throttled via the shared auth token-bucket limiter; a wrong credential returns an RFC 7807
/// <b>401</b> with a generic detail that never reveals whether the login exists (no user
/// enumeration).</para>
/// </summary>
[ApiController]
[Route("v1/partner/auth")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
// The login PRECEDES a session token (it mints one) — anonymous-by-design, bypasses L2. Mirrors
// AuthOtpController / TokensController (ADR-004 D1 / ADR-005 §A public).
[AllowAnonymous]
[PublicEndpoint("Partner login precedes the session token — it mints one — ADR-005 §A public.")]
public sealed class PartnerAuthController : ControllerBase
{
    private readonly IPartnerCredentialStore _credentials;
    private readonly IUsersStore _users;
    private readonly ITokenService _tokens;
    private readonly ILogger<PartnerAuthController> _log;

    public PartnerAuthController(
        IPartnerCredentialStore credentials,
        IUsersStore users,
        ITokenService tokens,
        ILogger<PartnerAuthController> log)
    {
        _credentials = credentials;
        _users = users;
        _tokens = tokens;
        _log = log;
    }

    /// <summary>
    /// POST /v1/partner/auth/login — verify an admin-provisioned partner credential and mint a gateway
    /// session (access + refresh) carrying the <c>partner</c> role.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(PartnerLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] PartnerLoginRequest request, CancellationToken ct)
    {
        // [ApiController] already 400s a missing/blank identifier or password (DataAnnotations).
        var account = await _credentials.VerifyAsync(request.Identifier, request.Password, ct);
        if (account is null)
        {
            // Uniform 401 for BOTH unknown-login and wrong-secret — never reveal which (no enumeration).
            // No PII in the log (only the outcome), and never the identifier/secret.
            _log.LogWarning("partner.auth.login rejected an invalid credential.");
            return Problem(
                title: "Invalid partner credentials.",
                detail: "The login or secret is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized,
                type: "https://jeeb.dev/errors/invalid-partner-credentials");
        }

        var userId = account.HolderId.ToString();

        // Project the partner identity locally BEFORE minting so the gateway-signed JWT embeds
        // roles=[partner] + active_role=partner (TokenService reads active_role from the store) —
        // mirroring the OTP-verify / super-login UpsertProjectionAsync step. Idempotent.
        await _users.UpsertProjectionAsync(new UserProfile
        {
            Id = userId,
            Phone = string.Empty,
            Name = account.DisplayName,
            Roles = new System.Collections.Generic.List<string> { Roles.Partner },
            ActiveRole = Roles.Partner,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);

        var pair = await _tokens.IssueAsync(userId, new[] { Roles.Partner }, ct);

        _log.LogInformation("partner.auth.login ok partnerId={PartnerId}", userId);

        var expiresInSeconds = (int)Math.Max(
            (pair.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0);

        return Ok(new PartnerLoginResponse
        {
            Token = pair.AccessToken,
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            TokenType = "Bearer",
            AccessTokenExpiresInSeconds = expiresInSeconds,
            AccessTokenExpiresAt = pair.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = pair.RefreshTokenExpiresAt,
            Partner = new PartnerProfileDto
            {
                PartnerId = account.HolderId,
                Login = account.Login,
                DisplayName = account.DisplayName,
                Role = Roles.Partner,
            },
        });
    }
}
