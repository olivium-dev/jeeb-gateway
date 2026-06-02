using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Phone sign-in via one-time-password (R1-OTP-SIGN-IN).
///
/// These two routes were referenced in the <see cref="JeebGateway.Controllers.OtpController"/>
/// docstring as "phone sign-in lives at <c>POST /v1/auth/otp/{request,verify}</c>"
/// but were never implemented — mobile sign-in could not complete. This controller
/// adds them ADDITIVELY:
///
///   - <c>POST /v1/auth/otp/request</c>  → 202 Accepted (OTP sent out-of-band via Twilio)
///   - <c>POST /v1/auth/otp/verify</c>   → 200 OK with <see cref="TokenPairResponse"/>
///
/// Both routes reuse the SAME NSwag-generated <see cref="IServiceOTPClient"/> that
/// <see cref="JeebGateway.Controllers.OtpController"/> and the delivery-handover
/// path already consume (wired to <c>ServiceOTPApi:BaseUrl</c> in
/// <c>appsettings.Production.json</c>). On a successful verify, tokens are minted
/// via the existing <see cref="ITokenService"/> — exactly the same path the
/// existing <c>POST /auth/tokens</c> route uses — and returned in the existing
/// <see cref="TokenPairResponse"/> shape (no new fields).
///
/// ADDITIVE: these are NEW route paths. No existing surface (<c>/api/otp</c>,
/// <c>/auth/tokens</c>, the delivery-handover OTP) is modified. Gated by the
/// existing <c>FeatureFlags:UseUpstream:Otp</c> kill switch, which fails closed
/// (503) when off — there is no in-memory fallback for a one-time-password service.
/// </summary>
[ApiController]
[Route("v1/auth/otp")]
[Produces("application/json", "application/problem+json")]
public sealed class AuthOtpController : ControllerBase
{
    private readonly IServiceOTPClient _otpClient;
    private readonly ITokenService _tokens;
    private readonly IUsersStore _users;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<AuthOtpController> _log;

    public AuthOtpController(
        IServiceOTPClient otpClient,
        ITokenService tokens,
        IUsersStore users,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<AuthOtpController> log)
    {
        _otpClient = otpClient;
        _tokens = tokens;
        _users = users;
        _flags = flags;
        _log = log;
    }

    /// <summary>
    /// POST /v1/auth/otp/request — trigger a sign-in OTP for a phone number scoped
    /// to an application id. The code is generated and delivered out-of-band by the
    /// OTP service (Twilio SMS); it is never on the wire. Returns 202 Accepted on a
    /// successful trigger and an empty body.
    /// </summary>
    [HttpPost("request")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Request([FromBody] AuthOtpRequestRequest? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (body is null
            || string.IsNullOrWhiteSpace(body.Phone)
            || string.IsNullOrWhiteSpace(body.ApplicationId))
        {
            return InvalidRequest("phone and applicationId are required.");
        }

        try
        {
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber = body.Phone,
                ApplicationId = body.ApplicationId
            }, ct);

            // Never log the phone or any OTP-adjacent data — only the
            // application partition (PR review B5 precedent in OtpController).
            _log.LogInformation(
                "Sign-in OTP request triggered for applicationId {ApplicationId}", body.ApplicationId);

            return Accepted();
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "request");
        }
    }

    /// <summary>
    /// POST /v1/auth/otp/verify — validate the user-entered code and, on success,
    /// mint a token pair via the existing <see cref="ITokenService"/>. Returns 200
    /// with a <see cref="TokenPairResponse"/>, 401 ProblemDetails on an invalid or
    /// expired code, 400 on a malformed request, 502 on an upstream fault.
    /// </summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(TokenPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Verify([FromBody] AuthOtpVerifyRequest? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (body is null
            || string.IsNullOrWhiteSpace(body.Phone)
            || string.IsNullOrWhiteSpace(body.OtpCode)
            || string.IsNullOrWhiteSpace(body.ApplicationId))
        {
            return InvalidRequest("phone, otpCode and applicationId are required.");
        }

        try
        {
            await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber = body.Phone,
                Otp = body.OtpCode,
                ApplicationId = body.ApplicationId
            }, ct);
        }
        catch (ApiException ex) when (ex.StatusCode is StatusCodes.Status401Unauthorized
                                          or StatusCodes.Status400BadRequest)
        {
            // The OTP service returns 401 for every validation failure. Surface as a
            // 401 ProblemDetails without echoing the upstream body (may embed the code).
            return Problem(
                title: "OTP validation failed",
                detail: "The provided one-time-password is invalid or has expired.",
                statusCode: StatusCodes.Status401Unauthorized,
                type: "https://datatracker.ietf.org/doc/html/rfc7235#section-3.1");
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "verify");
        }

        // OTP verified → mint tokens exactly like the existing /auth/tokens issue
        // path: ensure a profile shell exists, then issue against its current roles.
        var profile = await _users.GetOrCreateAsync(body.Phone, ct);
        var pair = await _tokens.IssueAsync(profile.Id, profile.Roles, ct);

        _log.LogInformation(
            "Sign-in OTP verified for applicationId {ApplicationId}", body.ApplicationId);

        return Ok(ToResponse(pair));
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

    private ObjectResult InvalidRequest(string detail) => Problem(
        title: "Invalid OTP sign-in request",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest,
        type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");

    private ObjectResult UpstreamDisabled() => Problem(
        title: "OTP service not enabled",
        detail: "The one-time-password upstream is not enabled in this environment "
              + "(FeatureFlags:UseUpstream:Otp is false).",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4");

    private ObjectResult UpstreamFault(ApiException ex, string operation)
    {
        _log.LogWarning(
            "Sign-in OTP {Operation} upstream failure: status {UpstreamStatus}",
            operation, ex.StatusCode);

        return Problem(
            title: "OTP service upstream failure",
            detail: $"The one-time-password service returned an unexpected status while handling '{operation}'.",
            statusCode: StatusCodes.Status502BadGateway,
            type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
    }
}

/// <summary>Request body for <c>POST /v1/auth/otp/request</c>.</summary>
public sealed record AuthOtpRequestRequest(string Phone, string ApplicationId);

/// <summary>Request body for <c>POST /v1/auth/otp/verify</c>.</summary>
public sealed record AuthOtpVerifyRequest(string Phone, string OtpCode, string ApplicationId);
