using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Auth.OtpSignIn;

// ---------------------------------------------------------------------------
// Phone sign-in OTP contract (frozen) for POST /v1/auth/otp/{request,verify}.
//
// These DTOs + the RFC 7807 Problem helper were lifted VERBATIM out of the
// retired [DevOnly] AuthOtpMockController so deleting the mock does not delete
// the contract shape. They are now consumed by the PRODUCTION
// AuthOtpController, which orchestrates ServiceOTPClient -> one-time-password
// for send/validate and mints the gateway session on success (JEB-1516).
//
// The wire shape is byte-identical to the recovered OLD contract the Jeeb
// mobile client / E2E console call:
//   request: { "phone": "..." }                 -> 200 { "ttlSeconds": 300 }
//   verify:  { "phone": "...", "code": "..." }
//            -> 200 { "accessToken", "refreshToken",
//                     "user": { "userId", "activeRole", "availableRoles" } }
//            -> 401 ProblemDetails type=.../invalid_otp   (wrong/expired/no record)
//            -> 502 ProblemDetails                          (upstream fault)
// ---------------------------------------------------------------------------

/// <summary>
/// RFC 7807 problem-type emitter for the phone sign-in OTP surface. Keeps the
/// FROZEN problem-type base URI (<c>https://problems.jeeb.lb/auth</c>) in one
/// place so the production controller and any future consumer stay byte-aligned
/// with the recovered contract. Moved here (out of the retired mock controller)
/// as part of JEB-1516.
/// </summary>
public static class OtpSignInProblems
{
    /// <summary>Frozen RFC 7807 problem-type base URI (recovered <c>OtpProblemTypes</c>).</summary>
    public const string ProblemBaseUri = "https://problems.jeeb.lb/auth";

    /// <summary>
    /// Build a <c>application/problem+json</c> result under the frozen
    /// <see cref="ProblemBaseUri"/> base. The upstream response body is NEVER
    /// passed in here — callers must supply a fixed, code-free detail string so
    /// no OTP-adjacent data can leak to the caller.
    /// </summary>
    public static ObjectResult Problem(
        ControllerBase c, int status, string shortType, string title, string detail)
        => Build(c, ProblemBaseUri, status, shortType, title, detail);

    /// <summary>Frozen RFC 7807 problem-type base URI for the S02 Wave-1 dual-role user surfaces.</summary>
    public const string UsersProblemBaseUri = "https://problems.jeeb.lb/users";

    /// <summary>
    /// Build a <c>application/problem+json</c> result under <see cref="UsersProblemBaseUri"/>
    /// for the dual-role <c>/v1/users/me</c> surfaces (F-A / F-B). Same code-free-detail
    /// contract as <see cref="Problem"/>.
    /// </summary>
    public static ObjectResult UsersProblem(
        ControllerBase c, int status, string shortType, string title, string detail)
        => Build(c, UsersProblemBaseUri, status, shortType, title, detail);

    private static ObjectResult Build(
        ControllerBase c, string baseUri, int status, string shortType, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"{baseUri}/{shortType}",
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

/// <summary>
/// ADDITIVE options for the phone sign-in OTP orchestration. Bound from the
/// <c>Auth:Otp</c> configuration section. The shared <c>one-time-password</c>
/// service keys its <c>Phone</c> rows by <c>ApplicationId</c>, so the Jeeb
/// gateway MUST always forward the Jeeb tenant's application id on every
/// send/validate. The id is configuration, NOT a hardcoded constant
/// (JEB-1516 §3.2).
/// </summary>
public sealed class OtpSignInOptions
{
    public const string SectionName = "Auth:Otp";

    /// <summary>
    /// The Jeeb tenant's application GUID in the shared one-time-password
    /// service. Sent as <c>applicationId</c> on every SendOTP / ValidateOTP.
    /// </summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>
    /// TTL (seconds) the gateway surfaces to the client on a successful
    /// <c>request</c>. The shared service does not return a TTL, so the gateway
    /// supplies this contract constant. Defaults to the frozen contract value
    /// (300). Configurable so an env can tune it without a code change.
    /// </summary>
    public int TtlSeconds { get; set; } = 300;
}
