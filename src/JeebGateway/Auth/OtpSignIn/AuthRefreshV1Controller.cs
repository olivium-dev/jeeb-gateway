using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// PRODUCTION refresh-rotation surface — <c>POST /v1/auth/refresh</c> (S02 F-D,
/// JEB-37 / JEB-1430). This is the <c>v1</c> route literal the Jeeb client / E2E
/// console call (the legacy <c>POST /auth/refresh</c> in the now-[Obsolete]
/// <see cref="JeebGateway.Controllers.AuthController"/> remains for older callers).
///
/// <para><b>Reuse, not rebuild.</b> The rotate-on-use + reuse-detection logic
/// already exists and is unchanged: this controller forwards to the existing
/// <see cref="ITokenService.RefreshAsync"/>, which (1) issues a new access+refresh
/// pair and revokes the presented token on success, and (2) on replay of an
/// already-rotated token, revokes the ENTIRE refresh-token family and returns
/// <see cref="RefreshOutcome.ReuseDetected"/>. The gateway is the sole signer of
/// the session JWT (token-authority invariant N11); this path does NOT touch
/// user-management.</para>
///
/// <para><b>Durable store (M3).</b> The behaviour here is independent of the
/// <see cref="IRefreshTokenStore"/> backing implementation; binding a durable
/// store (so reuse-detection survives a gateway bounce / spans replicas) is the
/// owner-gated store-tech choice and does not change this surface.</para>
///
/// <para>Outcomes: <c>200</c> with the rotated pair; <c>400 invalid_request</c>
/// when <c>refreshToken</c> is missing; <c>401</c> RFC 7807 for any
/// not-found / expired / revoked / <b>reuse-detected</b> token (the client must
/// re-OTP). The 401 body never distinguishes reuse from expiry to a caller — the
/// family revocation is the security action, not a disclosed signal.</para>
/// </summary>
[ApiController]
[Route("v1/auth")]
// NOTE: intentionally NO class-level [Produces(...)]. A [Produces] filter CLEARS an
// ObjectResult's own ContentTypes and forces the first listed media type, which
// downgraded the RFC 7807 error bodies emitted by OtpSignInProblems.Problem
// (ContentTypes = "application/problem+json") to "application/json". Omitting it lets
// each result carry its correct media type — success → application/json, error →
// application/problem+json — while the per-action [ProducesResponseType] still
// documents the shapes for Swagger (JEBV4-244).
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
// ADR-004 D1: public by design — refresh is authenticated by the rotation cookie, not a bearer.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public — refresh-rotation precedes/renews the session token; bypasses L2.
[PublicEndpoint("Refresh-rotation — token authenticated by the rotation credential, not a bearer (ADR-005 §A).")]
public sealed class AuthRefreshV1Controller : ControllerBase
{
    private readonly ITokenService _tokens;
    private readonly ILogger<AuthRefreshV1Controller> _log;

    public AuthRefreshV1Controller(ITokenService tokens, ILogger<AuthRefreshV1Controller> log)
    {
        _tokens = tokens;
        _log = log;
    }

    /// <summary>
    /// POST /v1/auth/refresh — rotate a refresh token. Reuse of a rotated token
    /// burns the whole family (401 → forces re-OTP).
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(RefreshPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return OtpSignInProblems.Problem(this, StatusCodes.Status400BadRequest, "invalid_request",
                "refreshToken is required", "A refreshToken must be supplied to rotate the session.");
        }

        var result = await _tokens.RefreshAsync(body.RefreshToken!, ct);
        if (result.Outcome == RefreshOutcome.Ok && result.Tokens is not null)
        {
            return Ok(new RefreshPairResponse
            {
                AccessToken = result.Tokens.AccessToken,
                RefreshToken = result.Tokens.RefreshToken,
            });
        }

        if (result.Outcome == RefreshOutcome.ReuseDetected)
        {
            // The family was revoked by the token service. Never disclose the
            // distinction to the caller — a uniform 401 forces a re-OTP.
            _log.LogWarning("auth.refresh reuse detected — refresh-token family revoked");
        }

        return OtpSignInProblems.Problem(this, StatusCodes.Status401Unauthorized, "invalid_refresh",
            "Refresh rejected", "The refresh token is invalid, expired, or has been revoked. Sign in again.");
    }

    /// <summary>
    /// POST /v1/auth/logout — revoke the presented refresh token so the session
    /// can no longer be rotated (JEBV4-244). This is the <c>v1</c> route literal
    /// the Jeeb client calls; it mirrors the legacy <c>POST /auth/logout</c> in
    /// the now-[Obsolete] <see cref="JeebGateway.Controllers.AuthController.Logout"/>.
    ///
    /// <para><b>Reuse, not rebuild.</b> A thin pass-through to the existing
    /// <see cref="ITokenService.RevokeAsync"/> with <see cref="RevocationReason.Logout"/>
    /// — the same refresh-token store as refresh; no new persistence, no new
    /// business logic, and (like refresh) this path does NOT touch
    /// user-management. Idempotent: an unknown / already-revoked token still
    /// returns <c>204</c> (the token service no-ops on a miss), never disclosing
    /// whether the token existed.</para>
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequestDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return OtpSignInProblems.Problem(this, StatusCodes.Status400BadRequest, "invalid_request",
                "refreshToken is required", "A refreshToken must be supplied to end the session.");
        }

        await _tokens.RevokeAsync(body.RefreshToken!, RevocationReason.Logout, ct);
        return NoContent();
    }
}

/// <summary>Request body for <c>POST /v1/auth/refresh</c>.</summary>
public sealed class RefreshRequestDto
{
    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
}

/// <summary>Response for <c>POST /v1/auth/refresh</c> — the rotated pair
/// (camelCase <c>accessToken</c>/<c>refreshToken</c>, byte-aligned with the
/// verify mint shape the S02 contract asserts).</summary>
public sealed class RefreshPairResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;
}
