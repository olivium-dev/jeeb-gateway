using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// T-BE-001 / JEB-471 — OTP sign-in surface presented to mobile.
///
/// Routes:
///   POST /v1/auth/otp/request   →  request an OTP (Twilio via downstream)
///   POST /v1/auth/otp/verify    →  verify OTP + issue JWT pair
///   POST /v1/auth/refresh       →  rotate refresh token (AC5b)
///
/// This controller is purposefully NOT marked [Obsolete] — it is the v3
/// surface, not a legacy BFF proxy. It does NOT live under
/// <see cref="JeebGateway.Controllers"/> (that namespace is in the
/// in-process [Obsolete] BFF migration band — see
/// <c>JeebGateway.csproj</c> NoWarn block).
///
/// Phone PII discipline (AC-PhonePIIHash):
///   - The raw phone is normalised, then HMAC-SHA256-hashed with a server
///     pepper (PR #32 review B1 — bcrypt was non-deterministic and broke
///     correlation); only the hash appears in logs, span tags, and metrics
///     labels.
///   - Even on failure paths (invalid_phone, invalid_country), the response
///     body NEVER echoes the raw phone — only a generic message.
/// </summary>
[ApiController]
[Route("v1/auth")]
[Produces("application/json", "application/problem+json")]
public sealed class AuthOtpController : ControllerBase
{
    private const string OtpPurposeLogin = "login";

    private readonly IPhoneNormalizer                    _normalizer;
    private readonly IPhoneHasher                        _hasher;
    private readonly IServiceOtpClient                   _otp;
    private readonly IUserManagementPhoneIdentityClient  _userMgmt;
    private readonly IJeebJwtIssuer                      _jwt;
    private readonly IOtpRequestRateLimiter              _limiter;
    private readonly TimeProvider                        _clock;
    private readonly ILogger<AuthOtpController>          _log;

    public AuthOtpController(
        IPhoneNormalizer normalizer,
        IPhoneHasher hasher,
        IServiceOtpClient otp,
        IUserManagementPhoneIdentityClient userMgmt,
        IJeebJwtIssuer jwt,
        IOtpRequestRateLimiter limiter,
        TimeProvider clock,
        ILogger<AuthOtpController> log)
    {
        _normalizer = normalizer;
        _hasher     = hasher;
        _otp        = otp;
        _userMgmt   = userMgmt;
        _jwt        = jwt;
        _limiter    = limiter;
        _clock      = clock;
        _log        = log;
    }

    // -----------------------------------------------------------------------
    // POST /v1/auth/otp/request
    // -----------------------------------------------------------------------
    [HttpPost("otp/request")]
    [ProducesResponseType(typeof(OtpRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestOtp([FromBody] OtpRequestDto? body, CancellationToken ct)
    {
        using var span = OtpSignInActivitySource.Source.StartActivity(
            OtpSignInActivitySource.SpanRequest, ActivityKind.Server);

        // AC-PhoneNorm + AC-ProblemTypeSet: classify and reject early.
        var (phone, region, problem) = NormalizeOrProblem(body?.Phone);
        if (problem is not null)
        {
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, problem.Type);
            span?.SetTag(OtpSignInActivitySource.TagPhoneNorm, false);
            return problem.ToResult(this);
        }

        var phoneHash = _hasher.HashE164(phone!);
        span?.SetTag(OtpSignInActivitySource.TagPhoneHash, phoneHash);
        span?.SetTag(OtpSignInActivitySource.TagPhoneNorm, true);
        span?.SetTag(OtpSignInActivitySource.TagPhoneRegion, region);

        // AC-GatewayRateLimit: per-phone (3/min) + per-IP (10/min).
        var ip       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var decision = _limiter.TryAcquire(phone!, ip);
        if (!decision.Allowed)
        {
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "rate_limited");
            _log.LogWarning(
                "auth.otp.request rate_limited dimension={Dimension} phoneHash={PhoneHash}",
                decision.LimitedDimension, phoneHash);

            // RFC 6585 §4: 429 SHOULD include Retry-After.
            Response.Headers["Retry-After"] = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return BuildProblem(
                status: StatusCodes.Status429TooManyRequests,
                type:   OtpProblemTypes.RateLimited,
                title:  "Too many requests",
                detail: "Please wait before requesting another code.");
        }

        // Downstream call. Any exception bubbles into a 502; the gateway
        // does NOT translate downstream 5xx into a token issuance.
        SendOtpResult sent;
        try
        {
            sent = await _otp.SendAsync(phone!, OtpPurposeLogin, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "auth.otp.request downstream send failed phoneHash={PhoneHash}", phoneHash);
            span?.SetStatus(ActivityStatusCode.Error);
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "service_unavailable");
            return BuildProblem(
                status: StatusCodes.Status502BadGateway,
                type:   OtpProblemTypes.ServiceUnavailable,
                title:  "Service temporarily unavailable",
                detail: "Could not request an OTP at this time. Try again shortly.");
        }

        span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "ok");
        span?.SetTag(OtpSignInActivitySource.TagDownstreamReused, sent.Reused);

        _log.LogInformation(
            "auth.otp.request ok phoneHash={PhoneHash} reused={Reused} expiresAt={ExpiresAt:O}",
            phoneHash, sent.Reused, sent.ExpiresAt);

        // AC1: ttlSeconds = ceil((ExpiresAt - now).TotalSeconds), where `now`
        // comes from the injected TimeProvider so the test-side FakeTimeProvider
        // controls the value deterministically. Audit #14764 locks ttl = 300s.
        var ttlSeconds = (int)Math.Max(
            Math.Round((sent.ExpiresAt - _clock.GetUtcNow()).TotalSeconds),
            0);
        return Ok(new OtpRequestResponse { TtlSeconds = ttlSeconds });
    }

    // -----------------------------------------------------------------------
    // POST /v1/auth/otp/verify
    // -----------------------------------------------------------------------
    [HttpPost("otp/verify")]
    [ProducesResponseType(typeof(OtpVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),    StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails),    StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails),    StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyDto? body, CancellationToken ct)
    {
        using var span = OtpSignInActivitySource.Source.StartActivity(
            OtpSignInActivitySource.SpanVerify, ActivityKind.Server);

        // Phone classification.
        var (phone, region, problem) = NormalizeOrProblem(body?.Phone);
        if (problem is not null)
        {
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, problem.Type);
            return problem.ToResult(this);
        }

        if (string.IsNullOrWhiteSpace(body?.Code))
        {
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "invalid_otp");
            return BuildProblem(
                status: StatusCodes.Status401Unauthorized,
                type:   OtpProblemTypes.InvalidOtp,
                title:  "Invalid code",
                detail: "The OTP code is missing or empty.");
        }

        var phoneHash = _hasher.HashE164(phone!);
        span?.SetTag(OtpSignInActivitySource.TagPhoneHash, phoneHash);
        span?.SetTag(OtpSignInActivitySource.TagPhoneNorm, true);
        span?.SetTag(OtpSignInActivitySource.TagPhoneRegion, region);

        // Validate against downstream OTP service.
        ValidateOtpResult validation;
        try
        {
            validation = await _otp.ValidateAsync(phone!, body.Code!, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "auth.otp.verify downstream validate failed phoneHash={PhoneHash}", phoneHash);
            span?.SetStatus(ActivityStatusCode.Error);
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "service_unavailable");
            return BuildProblem(
                status: StatusCodes.Status502BadGateway,
                type:   OtpProblemTypes.ServiceUnavailable,
                title:  "Service temporarily unavailable",
                detail: "Could not validate the OTP at this time.");
        }

        if (!validation.Success)
        {
            // AC3 + AC4 mapping.
            return MapValidationFailure(validation, phoneHash, span);
        }

        // Find-or-create the user in user-management (sibling story T-BE-001a).
        PhoneIdentityResponse identity;
        try
        {
            identity = await _userMgmt.PhoneIdentityFindOrCreateAsync(phone!, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Dev/local without the sibling story wired in — fail closed.
            _log.LogError(ex,
                "auth.otp.verify user-management not configured phoneHash={PhoneHash}", phoneHash);
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "service_unavailable");
            return BuildProblem(
                status: StatusCodes.Status503ServiceUnavailable,
                type:   OtpProblemTypes.ServiceUnavailable,
                title:  "Service temporarily unavailable",
                detail: "The user identity service is not yet configured.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "auth.otp.verify user-management call failed phoneHash={PhoneHash}", phoneHash);
            span?.SetStatus(ActivityStatusCode.Error);
            span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "service_unavailable");
            return BuildProblem(
                status: StatusCodes.Status502BadGateway,
                type:   OtpProblemTypes.ServiceUnavailable,
                title:  "Service temporarily unavailable",
                detail: "Could not look up the user identity.");
        }

        var pair = _jwt.Issue(
            userId:         identity.UserId,
            activeRole:     identity.ActiveRole,
            availableRoles: identity.AvailableRoles,
            phoneHash:      phoneHash);

        span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "ok");
        span?.SetTag(OtpSignInActivitySource.TagUserIsNew, identity.IsNew);

        _log.LogInformation(
            "auth.otp.verify ok userId={UserId} isNew={IsNew} phoneHash={PhoneHash}",
            identity.UserId, identity.IsNew, phoneHash);

        return Ok(new OtpVerifyResponse
        {
            AccessToken  = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            User = new OtpVerifyUserBlock
            {
                UserId         = pair.UserId,
                ActiveRole     = pair.ActiveRole,
                AvailableRoles = pair.AvailableRoles,
            },
        });
    }

    // -----------------------------------------------------------------------
    // POST /v1/auth/refresh
    // -----------------------------------------------------------------------
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(OtpVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),    StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] OtpRefreshDto? body, CancellationToken ct)
    {
        using var span = OtpSignInActivitySource.Source.StartActivity(
            OtpSignInActivitySource.SpanRefresh, ActivityKind.Server);

        if (string.IsNullOrWhiteSpace(body?.RefreshToken))
        {
            span?.SetTag(OtpSignInActivitySource.TagRefreshOutcome, "missing");
            // PR #32 review S3 — distinct from invalid_otp so mobile renders
            // the "session expired" copy, not "wrong code".
            return BuildProblem(
                status: StatusCodes.Status401Unauthorized,
                type:   OtpProblemTypes.InvalidRefreshToken,
                title:  "Invalid refresh token",
                detail: "Refresh token is missing or empty.");
        }

        var outcome = await _jwt.RefreshAsync(body.RefreshToken!, ct);
        switch (outcome.Outcome)
        {
            case RefreshOutcome.Ok when outcome.Pair is { } pair:
                span?.SetTag(OtpSignInActivitySource.TagRefreshOutcome, "ok");
                return Ok(new OtpVerifyResponse
                {
                    AccessToken  = pair.AccessToken,
                    RefreshToken = pair.RefreshToken,
                    User = new OtpVerifyUserBlock
                    {
                        UserId         = pair.UserId,
                        ActiveRole     = pair.ActiveRole,
                        AvailableRoles = pair.AvailableRoles,
                    },
                });

            case RefreshOutcome.ReuseDetected:
                span?.SetTag(OtpSignInActivitySource.TagRefreshOutcome, "reuse_detected");
                _log.LogWarning(
                    "auth.refresh family revoked due to detected reuse — caller must re-OTP");
                return BuildProblem(
                    status: StatusCodes.Status401Unauthorized,
                    type:   OtpProblemTypes.InvalidRefreshToken,
                    title:  "Refresh token reuse detected",
                    detail: "The refresh-token family has been revoked. Please sign in again.");

            default:
                span?.SetTag(OtpSignInActivitySource.TagRefreshOutcome, "revoked_or_invalid");
                return BuildProblem(
                    status: StatusCodes.Status401Unauthorized,
                    type:   OtpProblemTypes.InvalidRefreshToken,
                    title:  "Invalid refresh token",
                    detail: "The refresh token is expired, revoked, or invalid.");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private (string? Phone, string? Region, OtpProblem? Problem) NormalizeOrProblem(string? input)
    {
        var result = _normalizer.Normalize(input);
        return result.Outcome switch
        {
            PhoneNormalizationOutcome.Normalized
                => (result.E164, result.RegionCode, null),
            PhoneNormalizationOutcome.NonLebanese
                => (null, result.RegionCode, new OtpProblem(
                       StatusCodes.Status400BadRequest,
                       OtpProblemTypes.InvalidCountry,
                       "Unsupported country",
                       "Only Lebanese (+961) phone numbers are accepted during the pilot.")),
            _ => (null, null, new OtpProblem(
                       StatusCodes.Status400BadRequest,
                       OtpProblemTypes.InvalidPhone,
                       "Invalid phone number",
                       "The phone number could not be parsed.")),
        };
    }

    private IActionResult MapValidationFailure(
        ValidateOtpResult validation,
        string phoneHash,
        Activity? span)
    {
        switch (validation.ProblemType)
        {
            case "too_many_attempts":
                span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "too_many_attempts");
                _log.LogWarning(
                    "auth.otp.verify too_many_attempts phoneHash={PhoneHash}", phoneHash);
                return BuildProblem(
                    status: StatusCodes.Status429TooManyRequests,
                    type:   OtpProblemTypes.TooManyAttempts,
                    title:  "Too many attempts",
                    detail: "The OTP attempt limit has been reached. Request a new code.");

            case "invalid_otp":
            default:
                span?.SetTag(OtpSignInActivitySource.TagOtpOutcome, "invalid_otp");
                _log.LogInformation(
                    "auth.otp.verify invalid_otp phoneHash={PhoneHash}", phoneHash);
                return BuildProblem(
                    status: StatusCodes.Status401Unauthorized,
                    type:   OtpProblemTypes.InvalidOtp,
                    title:  "Invalid code",
                    detail: "The OTP code is incorrect or expired.");
        }
    }

    private IActionResult BuildProblem(int status, string type, string title, string detail)
    {
        // Manually constructed ProblemDetails so the `type` field carries the
        // canonical AC-ProblemTypeSet URI. Never include the raw phone in
        // detail/title — AC-PhonePIIHash.
        var problem = new ProblemDetails
        {
            Status   = status,
            Type     = type,
            Title    = title,
            Detail   = detail,
            Instance = HttpContext.Request.Path,
        };
        return new ObjectResult(problem)
        {
            StatusCode  = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    private sealed record OtpProblem(int Status, string Type, string Title, string Detail)
    {
        public IActionResult ToResult(AuthOtpController owner) =>
            owner.BuildProblem(Status, Type, Title, Detail);
    }
}
