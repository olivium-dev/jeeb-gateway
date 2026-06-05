using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// PRODUCTION phone sign-in OTP surface — <c>POST /v1/auth/otp/request</c> and
/// <c>POST /v1/auth/otp/verify</c>. This is the controller the (now-deleted)
/// <c>OtpController</c> xmldoc always promised at
/// <c>JeebGateway.Auth.OtpSignIn.AuthOtpController</c> (JEB-1516).
///
/// <para><b>Thin BFF — orchestration only.</b> The gateway holds NO OTP
/// send/validate logic. Code generation, persistence and validation live in the
/// shared <c>one-time-password</c> service; this controller orchestrates that
/// service through the SAME NSwag-generated <see cref="IServiceOTPClient"/> the
/// generic <c>/api/otp/*</c> proxy and the delivery-handover path consume, then
/// — and ONLY on a successful validate — mints the gateway session
/// (<see cref="IUsersStore.GetOrCreateAsync"/> + <see cref="ITokenService.IssueAsync"/>).
/// The session/JWT mint is orchestration, not OTP logic, and stays in the
/// gateway (JEB-1516 guardrail [05]). This replaces the retired in-gateway OTP
/// mock, which duplicated send/validate business logic (a P2 thin-BFF
/// violation).</para>
///
/// <para><b>Gating.</b> Gated by <c>FeatureFlags:UseUpstream:Otp</c>: when off
/// the controller returns <b>503</b> (fails closed; there is no localhost
/// fallback for a one-time-password service), mirroring
/// <c>OtpController.UpstreamDisabled()</c>. NOT <c>[DevOnly]</c> — this is the
/// live production surface and answers whenever <c>Features:DevEndpoints:Enabled</c>
/// is <c>false</c> (the committed value in every environment).</para>
///
/// <para><b>Application id.</b> The Jeeb tenant's application id is forwarded on
/// every send/validate from <see cref="OtpSignInOptions.ApplicationId"/>
/// (config <c>Auth:Otp:ApplicationId</c>), never hardcoded — the shared service
/// keys <c>Phone</c> rows by application id (JEB-1516 §3.2).</para>
/// </summary>
[ApiController]
[Route("v1/auth/otp")]
[Produces("application/json", "application/problem+json")]
public sealed class AuthOtpController : ControllerBase
{
    private readonly IServiceOTPClient _otpClient;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IOptions<OtpSignInOptions> _options;
    private readonly IUsersStore _users;
    private readonly ITokenService _tokens;
    private readonly IPhonePolicy _phonePolicy;
    private readonly IOtpRequestRateLimiter _rateLimiter;
    private readonly IUserManagementDualRoleClient _userManagement;
    private readonly ILogger<AuthOtpController> _log;

    public AuthOtpController(
        IServiceOTPClient otpClient,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IOptions<OtpSignInOptions> options,
        IUsersStore users,
        ITokenService tokens,
        IPhonePolicy phonePolicy,
        IOtpRequestRateLimiter rateLimiter,
        IUserManagementDualRoleClient userManagement,
        ILogger<AuthOtpController> log)
    {
        _otpClient = otpClient;
        _flags = flags;
        _options = options;
        _users = users;
        _tokens = tokens;
        _phonePolicy = phonePolicy;
        _rateLimiter = rateLimiter;
        _userManagement = userManagement;
        _log = log;
    }

    /// <summary>
    /// POST /v1/auth/otp/request — request a one-time-password for a phone.
    /// Orchestrates <see cref="IServiceOTPClient.SendOTPAsync(SendOTPRequestUserID?, CancellationToken)"/>
    /// against the shared service (which generates + persists + delivers the code
    /// out-of-band), then returns the contract's deterministic
    /// <c>{ ttlSeconds }</c>. The code is never on the wire.
    /// </summary>
    [HttpPost("request")]
    [ProducesResponseType(typeof(OtpRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RequestOtp([FromBody] OtpRequestDto? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (body is null || string.IsNullOrWhiteSpace(body.Phone))
        {
            return OtpSignInProblems.Problem(this, StatusCodes.Status400BadRequest, "invalid_phone",
                "Invalid phone number", "phone is required.");
        }

        // F-E (S02, JEB-37) — gateway-local phone admission policy, evaluated
        // BEFORE the upstream is dialed (no upstream change). Parse-first so a
        // malformed number is invalid_phone, not invalid_country (N4 vs N3).
        var policy = _phonePolicy.Evaluate(body.Phone);
        switch (policy.Outcome)
        {
            case PhonePolicyOutcome.InvalidPhone:
                return OtpSignInProblems.Problem(this, StatusCodes.Status400BadRequest, "invalid_phone",
                    "Invalid phone number", "The phone number is not a valid E.164 number.");
            case PhonePolicyOutcome.InvalidCountry:
                return OtpSignInProblems.Problem(this, StatusCodes.Status400BadRequest, "invalid_country",
                    "Unsupported country", "Sign-in is currently available only for Lebanese (LB) phone numbers.");
        }

        // F-E burst guard — per-IP AND per-phone sliding window. A throttled
        // request trips 429 rate_limited and MUST NOT dial the upstream (so a
        // throttle never costs an SMS; assertion-provable: SendOTP not called).
        var clientIp = JeebGateway.Security.RateLimitingExtensions.ResolveClientIp(HttpContext);
        if (!_rateLimiter.TryAcquire(clientIp, body.Phone))
        {
            return OtpSignInProblems.Problem(this, StatusCodes.Status429TooManyRequests, "rate_limited",
                "Too many requests", "Too many OTP requests. Please wait before requesting another code.");
        }

        try
        {
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber = body.Phone!,
                ApplicationId = _options.Value.ApplicationId,
            }, ct);

            // Never log the phone or any OTP-adjacent data — only the application
            // partition (PR review B5 precedent in DeliveriesController/OtpController).
            _log.LogInformation(
                "auth.otp.request triggered for applicationId {ApplicationId}",
                _options.Value.ApplicationId);

            // The shared service does not return a TTL; the gateway supplies the
            // contract's ttlSeconds constant.
            return Ok(new OtpRequestResponse { TtlSeconds = _options.Value.TtlSeconds });
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "request");
        }
    }

    /// <summary>
    /// POST /v1/auth/otp/verify — validate a user-entered code, and on success
    /// mint the gateway session. Orchestrates
    /// <see cref="IServiceOTPClient.ValidateOTPAsync(ValidateOTPRequestModel?, CancellationToken)"/>;
    /// on a clean validate it finds-or-creates the user (keyed on the normalized
    /// phone, matching the historical sign-in path) and issues an access+refresh
    /// pair via the existing <see cref="ITokenService"/>. A validation failure
    /// (the shared service returns a uniform 401/400 for wrong/expired/no-record/
    /// too-many-attempts) maps to <b>401</b> <c>invalid_otp</c>; the upstream body
    /// is NEVER echoed (it may embed the submitted code).
    /// </summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(OtpVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyDto? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (body is null || string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrWhiteSpace(body.Code))
        {
            return OtpSignInProblems.Problem(this, StatusCodes.Status401Unauthorized, "invalid_otp",
                "Invalid code", "The OTP code is missing or empty.");
        }

        try
        {
            await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber = body.Phone!,
                Otp = body.Code!,
                ApplicationId = _options.Value.ApplicationId,
            }, ct);
        }
        catch (ApiException ex) when (ex.StatusCode is StatusCodes.Status401Unauthorized
                                          or StatusCodes.Status400BadRequest)
        {
            // The OTP service returns a uniform 401/400 for every validation
            // failure (wrong code, expired, no record, too-many-attempts).
            // Surface as the frozen 401 invalid_otp ProblemDetails WITHOUT echoing
            // the upstream body (which may embed the submitted code).
            return OtpSignInProblems.Problem(this, StatusCodes.Status401Unauthorized, "invalid_otp",
                "Invalid code", "The OTP code is incorrect or expired.");
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "verify");
        }

        // Validate succeeded → resolve the identity and mint a REAL gateway session.
        //
        // F-C (S02 Wave-1, ADR-003): user-management is the identity authority. The
        // gateway no longer INVENTS the identity from the raw phone in an in-memory
        // store — it orchestrates UM's phone-keyed find-or-create, which returns the
        // canonical user id and the user's OPAQUE roles ({customer,driver}). The
        // gateway then TRANSLATES opaque -> snake_case Jeeb contract ({client,jeeber})
        // for the response body and STILL signs the sign-in session itself (the JWT
        // mint is orchestration and stays in the gateway — N11 split-signer: only the
        // role-switch path is UM-signed).
        var key = (body.Phone ?? string.Empty).Trim();
        var (userId, opaqueRoles, opaqueActiveRole) = await ResolveIdentityAsync(key, ct);

        // Project the UM-resolved identity locally so the gateway-minted JWT embeds the
        // SAME active_role/roles claims UM persisted (TokenService reads active_role from
        // the store). New identities default to the opaque 'customer' single role.
        await _users.UpsertProjectionAsync(new UserProfile
        {
            Id = userId,
            Phone = key,
            Name = string.Empty,
            Roles = opaqueRoles.ToList(),
            ActiveRole = opaqueActiveRole,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);

        var pair = await _tokens.IssueAsync(userId, opaqueRoles, ct);

        // Never log the raw phone or code — only the minted user id.
        _log.LogInformation("auth.otp.verify ok userId={UserId}", userId);

        // Translate the OPAQUE roles to the frozen snake_case Jeeb contract on the way out.
        var contractRoles = JeebRoleTranslator.ToContract(opaqueRoles);
        var contractActive = JeebRoleTranslator.ToContract(opaqueActiveRole);

        return Ok(new OtpVerifyResponse
        {
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            User = new OtpVerifyUserBlock
            {
                UserId = userId,
                ActiveRole = string.IsNullOrWhiteSpace(contractActive)
                    ? JeebRoleTranslator.ContractClient
                    : contractActive,
                AvailableRoles = contractRoles.Length > 0
                    ? contractRoles
                    : new[] { JeebRoleTranslator.ContractClient },
            },
        });
    }

    /// <summary>
    /// F-C identity resolution. When the UM kill switch is ON, orchestrates the shared
    /// user-management phone find-or-create (the identity authority) and returns the
    /// canonical id + OPAQUE roles. Degrades SAFELY: a transient UM fault falls back to
    /// the legacy in-memory find-or-create so a live OTP login is never hard-broken by a
    /// UM blip (the session is gateway-minted in both branches). When the switch is OFF,
    /// uses the in-memory path directly (unchanged legacy behavior for existing fixtures).
    /// </summary>
    private async Task<(string userId, IReadOnlyList<string> opaqueRoles, string opaqueActiveRole)>
        ResolveIdentityAsync(string phone, CancellationToken ct)
    {
        if (_flags.CurrentValue.UserManagement)
        {
            try
            {
                var um = await _userManagement.PhoneFindOrCreateAsync(phone, ct);
                var roles = um.AvailableRoles is { Count: > 0 } ? um.AvailableRoles : new[] { Roles.Client };
                var active = string.IsNullOrWhiteSpace(um.ActiveRole) ? Roles.Client : um.ActiveRole;
                return (um.UserId, roles, active);
            }
            catch (UserManagementCallException ex)
            {
                // Fail-safe: never block a successful OTP validate on a UM blip.
                _log.LogWarning(
                    "auth.otp.verify UM find-or-create failed (status {Status}); falling back to in-memory identity",
                    ex.StatusCode);
            }
        }

        var profile = await _users.GetOrCreateAsync(phone, ct);
        var fallbackActive = string.IsNullOrWhiteSpace(profile.ActiveRole) ? Roles.Client : profile.ActiveRole;
        return (profile.Id, profile.Roles.ToList(), fallbackActive);
    }

    /// <summary>
    /// 503 ProblemDetails when the OTP upstream kill switch is off. There is no
    /// in-memory fallback for a one-time-password service, so the surface fails
    /// closed rather than calling a localhost default (mirrors
    /// <c>OtpController.UpstreamDisabled()</c>).
    /// </summary>
    private ObjectResult UpstreamDisabled() => OtpSignInProblems.Problem(
        this, StatusCodes.Status503ServiceUnavailable, "otp_unavailable",
        "OTP service not enabled",
        "The one-time-password upstream is not enabled in this environment "
        + "(FeatureFlags:UseUpstream:Otp is false).");

    /// <summary>
    /// Maps an upstream <see cref="ApiException"/> to a 502 ProblemDetails. The
    /// upstream response body is NEVER forwarded to the caller or the log — it
    /// may contain OTP-adjacent data.
    /// </summary>
    private ObjectResult UpstreamFault(ApiException ex, string operation)
    {
        _log.LogWarning(
            "auth.otp {Operation} upstream failure: status {UpstreamStatus}",
            operation, ex.StatusCode);

        return OtpSignInProblems.Problem(
            this, StatusCodes.Status502BadGateway, "upstream_fault",
            "OTP service upstream failure",
            $"The one-time-password service returned an unexpected status while handling '{operation}'.");
    }
}
