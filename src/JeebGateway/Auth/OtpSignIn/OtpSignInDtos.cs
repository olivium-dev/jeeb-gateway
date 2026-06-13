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

    /// <summary>
    /// Build a <b>429 Too Many Requests</b> <c>application/problem+json</c> result
    /// under the frozen auth <see cref="ProblemBaseUri"/> and, when a positive
    /// retry hint is supplied, stamp the standard <c>Retry-After</c> response
    /// header (RFC 7231 §7.1.3, delta-seconds form) plus a mirrored
    /// <c>retryAfter</c> ProblemDetails extension so JSON-only clients can read it.
    ///
    /// <para>Used by the OTP sign-in surface to PROPAGATE an upstream
    /// one-time-password 429 (request-burst <c>rate_limited</c> /
    /// verify-too-many-attempts <c>too_many_attempts</c>) as a gateway 429 — the
    /// gateway never echoes the upstream body, only the machine
    /// <paramref name="shortType"/> code and the back-off hint.</para>
    /// </summary>
    public static ObjectResult TooManyRequests(
        ControllerBase c, string shortType, string title, string detail, int? retryAfterSeconds = null)
    {
        var result = Build(
            c, ProblemBaseUri, StatusCodes.Status429TooManyRequests, shortType, title, detail,
            extensions: retryAfterSeconds is > 0
                ? new Dictionary<string, object?> { ["retryAfter"] = retryAfterSeconds }
                : null);

        if (retryAfterSeconds is > 0)
        {
            // delta-seconds form; harmless if the header already exists (we set, not add).
            c.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.Value.ToString();
        }

        return result;
    }

    private static ObjectResult Build(
        ControllerBase c, string baseUri, int status, string shortType, string title, string detail,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"{baseUri}/{shortType}",
            Title = title,
            Detail = detail,
            Instance = c.HttpContext.Request.Path,
        };

        if (extensions is not null)
        {
            foreach (var kv in extensions)
                problem.Extensions[kv.Key] = kv.Value;
        }

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

    // S02 contract (ADR-003 / SEED-WIRING-SPEC): the verify user block uses the
    // frozen snake_case Jeeb contract keys, identical to GET /v1/users/me and the
    // role/switch user block. Harness H-A2/H-B2 assert $.user.available_roles /
    // $.user.active_role — these MUST be snake_case, not camelCase.
    [JsonPropertyName("active_role")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("available_roles")]
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

/// <summary>
/// Additive, default-off hardening for the OTP sign-in identity-resolution read
/// path (<c>POST /v1/auth/otp/verify</c>). Bound from <c>Security:Auth</c>.
///
/// <para><b>Why this exists.</b> When <c>FeatureFlags:UseUpstream:UserManagement</c>
/// is ON, the gateway resolves the canonical identity (and the user's available
/// roles) from user-management's phone find-or-create. The legacy behavior
/// (<see cref="FailClosedIdentityResolve"/> = <c>false</c>) DEGRADES on a UM fault
/// by silently falling back to the in-memory store, which mints a DIFFERENT user
/// id and a stale single <c>client</c> role. That is correct for keeping a live
/// login working through a transient UM blip, but it is WRONG for any flow that
/// depends on UM being the authority for <c>available_roles</c> (e.g. the
/// post-KYC <c>jeeber</c> grant): the fallback row never carries the granted role,
/// so the user can never switch to it. Lease evidence
/// <c>jeeb-20260613002036-8874</c> reproduced exactly this at S02/H-B2 on a virgin
/// env: KYC approve grants <c>jeeber</c>, but OTP re-login read
/// <c>available_roles=["client"]</c>.</para>
///
/// <para><b>Behavior when ON.</b> A UM find-or-create fault on the verify read path
/// fails CLOSED — the gateway returns <b>503</b> <c>otp_unavailable</c>
/// ProblemDetails instead of downgrading to the stale in-memory identity. The OTP
/// code was still consumed upstream, so the client simply re-requests; it never
/// receives a half-resolved session with the wrong roles. Default stays
/// <c>false</c> so existing environments and fixtures are unchanged until an
/// operator opts in (additive/default-off per the org options pattern).</para>
/// </summary>
public sealed class FailClosedIdentityResolveOptions
{
    public const string SectionName = "Security:Auth";

    /// <summary>
    /// When <c>true</c> AND <c>UseUpstream:UserManagement</c> is on, a UM
    /// find-or-create fault on the OTP verify read path returns 503
    /// <c>otp_unavailable</c> rather than falling back to the stale in-memory
    /// identity. Defaults to <c>false</c> (legacy degrade-to-in-memory behavior).
    /// </summary>
    public bool FailClosedIdentityResolve { get; set; }
}

/// <summary>
/// Raised by the OTP verify identity-resolution read path when
/// <see cref="FailClosedIdentityResolveOptions.FailClosedIdentityResolve"/> is on
/// and the user-management find-or-create faulted. The controller maps this to a
/// 503 <c>otp_unavailable</c> ProblemDetails (fail-closed) instead of degrading to
/// the in-memory identity. Carries the upstream status purely for the structured
/// log; the value is never echoed to the caller.
/// </summary>
public sealed class OtpIdentityUnavailableException : Exception
{
    public int UpstreamStatus { get; }

    public OtpIdentityUnavailableException(int upstreamStatus)
        : base($"Identity resolution failed closed (user-management status {upstreamStatus}).")
        => UpstreamStatus = upstreamStatus;
}
