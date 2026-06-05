using System.Text.Json.Serialization;
using JeebGateway.Security;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Auth.OtpDevMock;

/// <summary>
/// ADDITIVE restoration of the phone sign-in OTP routes
/// <c>POST /v1/auth/otp/request</c> and <c>POST /v1/auth/otp/verify</c>, which
/// were silently dropped from <c>main</c> by the lost-merge of PR #51
/// (<c>3e2720a</c>). The routes are restored at the EXACT OLD contract the Jeeb
/// mobile client / E2E console call (see
/// <c>jeeb-scenarios/e2e/e2e-S02-onboarding-signin-dual-role.md</c> H-A1/H-A2 and
/// the recovered <c>OtpSignInDtos</c> / <c>OtpProblemTypes</c> at <c>4a42cbd</c>):
///
///   request:  body <c>{ "phone": "..." }</c>            → 200 <c>{ "ttlSeconds": 300 }</c>
///   verify:   body <c>{ "phone": "...", "code": "..." }</c>
///             → 200 <c>{ "accessToken", "refreshToken", "user": { "userId", "activeRole", "availableRoles" } }</c>
///             → 401 ProblemDetails <c>type=.../invalid_otp</c>        (wrong / expired code)
///             → 429 ProblemDetails <c>type=.../too_many_attempts</c>  (attempt cap)
///
/// <para><b>Gating.</b> The whole controller carries <see cref="DevOnlyAttribute"/>:
/// every action returns <b>404</b> unless <c>Features:DevEndpoints:Enabled</c> is
/// explicitly <c>true</c> (committed <c>false</c> in EVERY environment, including
/// production — so prod sees no behaviour change). When ON, the routes are backed
/// by the credential-free <see cref="IDevOtpMock"/>: no upstream
/// <c>one-time-password</c> call, no Twilio, no SMS.</para>
///
/// <para>This is purely additive — it restores deleted route paths at their
/// original shape and reshapes nothing on any other surface.</para>
/// </summary>
[ApiController]
[DevOnly]
[Route("v1/auth/otp")]
[Produces("application/json", "application/problem+json")]
public sealed class AuthOtpMockController : ControllerBase
{
    private readonly IDevOtpMock _mock;
    private readonly ILogger<AuthOtpMockController> _log;

    public AuthOtpMockController(IDevOtpMock mock, ILogger<AuthOtpMockController> log)
    {
        _mock = mock;
        _log = log;
    }

    /// <summary>POST /v1/auth/otp/request — mock OTP request; deterministic ttl, no SMS.</summary>
    [HttpPost("request")]
    [ProducesResponseType(typeof(OtpRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult RequestOtp([FromBody] OtpRequestDto? body)
        => DevOtpEndpoints.Request(this, _mock, body);

    /// <summary>POST /v1/auth/otp/verify — mock OTP verify; fixed code mints a real session.</summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(OtpVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> VerifyOtp([FromBody] OtpVerifyDto? body, CancellationToken ct)
        => DevOtpEndpoints.VerifyAsync(this, _mock, _log, body, ct);
}

// ---------------------------------------------------------------------------
// Shared request/response handling — reused by AuthOtpMockController (the
// restored /v1/auth/otp/* routes) AND by the deprecated /api/otp alias path so
// both surfaces speak the IDENTICAL old contract through the SAME mock.
// ---------------------------------------------------------------------------

/// <summary>
/// Pure helpers that implement the restored OTP contract against
/// <see cref="IDevOtpMock"/>. Kept controller-agnostic so the deprecated
/// <c>/api/otp/send|validate</c> aliases can delegate here without duplicating
/// the contract (S02 ALT-4 — the aliases must map to the same handler/shape).
/// </summary>
public static class DevOtpEndpoints
{
    /// <summary>Frozen RFC 7807 problem-type base URI (recovered <c>OtpProblemTypes</c>).</summary>
    public const string ProblemBaseUri = "https://problems.jeeb.lb/auth";

    public static IActionResult Request(ControllerBase c, IDevOtpMock mock, OtpRequestDto? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Phone))
        {
            return Problem(c, StatusCodes.Status400BadRequest, "invalid_phone",
                "Invalid phone number", "phone is required.");
        }

        var result = mock.RequestAsync(body.Phone!);
        return c.Ok(new OtpRequestResponse { TtlSeconds = result.TtlSeconds });
    }

    public static async Task<IActionResult> VerifyAsync(
        ControllerBase c, IDevOtpMock mock, ILogger log, OtpVerifyDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrWhiteSpace(body.Code))
        {
            return Problem(c, StatusCodes.Status401Unauthorized, "invalid_otp",
                "Invalid code", "The OTP code is missing or empty.");
        }

        var result = await mock.VerifyAsync(body.Phone!, body.Code!, ct);
        switch (result.Outcome)
        {
            case DevOtpVerifyOutcome.Ok:
                // Never log the raw phone or code — only the minted user id.
                log.LogInformation("dev-mock auth.otp.verify ok userId={UserId}", result.UserId);
                return c.Ok(new OtpVerifyResponse
                {
                    AccessToken = result.AccessToken!,
                    RefreshToken = result.RefreshToken!,
                    User = new OtpVerifyUserBlock
                    {
                        UserId = result.UserId!,
                        ActiveRole = result.ActiveRole!,
                        AvailableRoles = result.AvailableRoles ?? Array.Empty<string>(),
                    },
                });

            case DevOtpVerifyOutcome.TooManyAttempts:
                return Problem(c, StatusCodes.Status429TooManyRequests, "too_many_attempts",
                    "Too many attempts", "The OTP attempt limit has been reached. Request a new code.");

            case DevOtpVerifyOutcome.InvalidOtp:
            default:
                return Problem(c, StatusCodes.Status401Unauthorized, "invalid_otp",
                    "Invalid code", "The OTP code is incorrect or expired.");
        }
    }

    private static ObjectResult Problem(ControllerBase c, int status, string shortType, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"{ProblemBaseUri}/{shortType}",
            Title = title,
            Detail = detail,
            Instance = c.HttpContext.Request.Path,
        };
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}

// ---------------------------------------------------------------------------
// DTOs — restored verbatim from the OLD contract (recovered OtpSignInDtos).
// ---------------------------------------------------------------------------

/// <summary>Request body for <c>POST /v1/auth/otp/request</c>.</summary>
public sealed class OtpRequestDto
{
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}

/// <summary>Request body for <c>POST /v1/auth/otp/verify</c>.</summary>
public sealed class OtpVerifyDto
{
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>Response for <c>POST /v1/auth/otp/request</c> — deterministic ttl.</summary>
public sealed class OtpRequestResponse
{
    [JsonPropertyName("ttlSeconds")]
    public int TtlSeconds { get; init; }
}

/// <summary>Response for <c>POST /v1/auth/otp/verify</c> — the minted session.</summary>
public sealed class OtpVerifyResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public OtpVerifyUserBlock User { get; init; } = new();
}

/// <summary>The user block inside <see cref="OtpVerifyResponse"/>.</summary>
public sealed class OtpVerifyUserBlock
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("activeRole")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("availableRoles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();
}
