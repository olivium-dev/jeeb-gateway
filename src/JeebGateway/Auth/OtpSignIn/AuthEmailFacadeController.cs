using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.service.ServiceUserManagement;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using GwUsersStore = JeebGateway.Users.IUsersStore;
using GwUserProfile = JeebGateway.Users.UserProfile;
using GwDualRoleClient = JeebGateway.Users.IUserManagementDualRoleClient;
using GwSeededRoles = JeebGateway.Users.IDevSeededRoleStore;
using GwRoles = JeebGateway.Users.Roles;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// EMAIL/AUTH FACADE (audit B2–B7). The Jeeb mobile email-login screen calls
/// <c>POST /v1/auth/login</c>, <c>/v1/auth/signup</c>, <c>/v1/auth/set-password</c>,
/// <c>/v1/auth/recovery[/request|/verify]</c> and <c>/v1/auth/social</c>. Those
/// paths were never mounted on the gateway, so every call dead-ended on an EMPTY
/// 404 (route not registered). This controller adds thin, ANONYMOUS routes that
/// proxy each to the REAL user-management upstream via the same NSwag-generated
/// <see cref="UmClient"/> the legacy <c>/api/User/*</c> proxy already consumes:
///
/// <list type="bullet">
///   <item><c>login</c>      → UM <c>POST api/User/login</c>           (LoginAsync)</item>
///   <item><c>signup</c>     → UM <c>POST api/User/register</c>        (RegisterAsync)</item>
///   <item><c>set-password</c>→ UM <c>POST api/User/password/reset</c> (ResetAsync)</item>
///   <item><c>recovery</c> / <c>recovery/request</c> → UM <c>POST api/User/password/forgot</c> (ForgotAsync)</item>
///   <item><c>social</c>     → UM <c>POST api/User/social</c>          (SocialAsync)</item>
/// </list>
///
/// <para><b>Thin BFF — orchestration only.</b> No auth business logic lives here:
/// user-management remains the identity/credential authority. For the legs that
/// establish a session (login / signup / set-password / social) the gateway
/// re-mints a REAL gateway session JWT (<c>iss=jeeb-gateway, aud=jeeb-clients</c>)
/// via <see cref="ITokenService.IssueAsync"/> — IDENTICAL in kind to the OTP and
/// super-login session mints — because UM tokens carry <c>aud=user-management</c>
/// and would 401 on the gateway <c>/v1/*</c> routes. The wire shape matches the
/// mobile <c>DioAuthRepository</c> contract:
/// <c>{ accessToken, refreshToken, user: { userId, email?, status? } }</c>.</para>
///
/// <para>Anonymous by design (these precede any session token). An upstream
/// fault is surfaced with the upstream status (401 invalid credentials, 409
/// email collision, 400 bad request) WITHOUT echoing the upstream body.</para>
/// </summary>
[ApiController]
[Route("v1/auth")]
// NOTE (JEBV4-261): intentionally NO class-level [Produces(...)]. The single-arg
// [Produces("application/json")] here was the worst offender — it CLEARED every
// ObjectResult's ContentTypes and forced "application/json", so the RFC 7807 error
// bodies emitted by OtpSignInProblems (ContentTypes = "application/problem+json") never
// reached the caller as problem+json at all. Omitting it lets errors carry
// application/problem+json while success stays application/json (the negotiated default).
// Mirrors the AuthRefreshV1Controller fix (PR #242).
public sealed class AuthEmailFacadeController : ControllerBase
{
    private readonly UmClient _um;
    private readonly ITokenService _tokens;
    private readonly GwUsersStore _users;
    private readonly GwDualRoleClient _userManagement;
    private readonly GwSeededRoles _seededRoles;
    private readonly ILogger<AuthEmailFacadeController> _log;

    public AuthEmailFacadeController(
        UmClient um,
        ITokenService tokens,
        GwUsersStore users,
        GwDualRoleClient userManagement,
        GwSeededRoles seededRoles,
        ILogger<AuthEmailFacadeController> log)
    {
        _um = um;
        _tokens = tokens;
        _users = users;
        _userManagement = userManagement;
        _seededRoles = seededRoles;
        _log = log;
    }

    // ----------------------------------------------------------------- login
    [AllowAnonymous]
    [PublicEndpoint("Email+password login — proxies UM api/User/login, mints gateway session.")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] EmailLoginDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Problem(400, "bad_request", "Missing credentials", "email and password are required.");

        try
        {
            var res = await _um.LoginAsync(new LoginRequest { Email = body.Email, Password = body.Password }, ct);
            return await MintSessionAsync(res?.UserId, body.Email, status: null, ct);
        }
        catch (UmApiException ex) { return Upstream(ex, "login"); }
    }

    // ---------------------------------------------------------------- signup
    [AllowAnonymous]
    [PublicEndpoint("Email signup — proxies UM api/User/register, mints gateway session.")]
    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] EmailSignupDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Problem(400, "bad_request", "Missing fields", "email and password are required.");

        try
        {
            var res = await _um.RegisterAsync(new RegisterUserRequest
            {
                Email = body.Email,
                Password = body.Password,
                ConfirmPassword = body.Password,
                Username = string.IsNullOrWhiteSpace(body.Name) ? body.Email : body.Name,
            }, ct);
            return await MintSessionAsync(res?.UserId, res?.Email ?? body.Email, res?.Status, ct);
        }
        catch (UmApiException ex) { return Upstream(ex, "signup"); }
    }

    // ---------------------------------------------------------- set-password
    [AllowAnonymous]
    [PublicEndpoint("Set/reset password — proxies UM api/User/password/reset, mints gateway session.")]
    [HttpPost("set-password")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Password))
            return Problem(400, "bad_request", "Missing password", "password is required.");

        try
        {
            var res = await _um.ResetAsync(new ResetPasswordRequest
            {
                Token = body.ResetToken,
                NewPassword = body.Password,
                ConfirmPassword = body.Password,
            }, ct);

            if (res is not null && res.Success)
            {
                // UM reset returns no session. Re-establish one via login so the
                // screen lands authenticated (matches the mobile contract).
                if (!string.IsNullOrWhiteSpace(body.Email))
                {
                    try
                    {
                        var login = await _um.LoginAsync(
                            new LoginRequest { Email = body.Email, Password = body.Password }, ct);
                        return await MintSessionAsync(login?.UserId, body.Email, status: null, ct);
                    }
                    catch (UmApiException)
                    {
                        // Reset succeeded but auto-login failed — report success without a session.
                    }
                }
                return Ok(new { success = true });
            }

            return Problem(400, "set_password_failed", "Set password failed",
                "The password could not be set.");
        }
        catch (UmApiException ex) { return Upstream(ex, "set-password"); }
    }

    // --------------------------------------------------- recovery (forgot pw)
    [AllowAnonymous]
    [PublicEndpoint("Forgot-password — proxies UM api/User/password/forgot.")]
    [HttpPost("recovery")]
    [HttpPost("recovery/request")]
    public async Task<IActionResult> Recovery([FromBody] RecoveryDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Email))
            return Problem(400, "bad_request", "Missing email", "email is required.");

        try
        {
            await _um.ForgotAsync(new ForgotPasswordRequest { Email = body.Email }, ct);
            // UM returns { success }. The mobile contract expects { requestId, expiresInSeconds }.
            // No verifiable requestId is surfaced upstream; return a stable shape so the screen
            // advances to the code-entry step (the code itself is delivered out of band by UM).
            return Ok(new { requestId = Guid.NewGuid().ToString("N"), expiresInSeconds = 600 });
        }
        catch (UmApiException ex) { return Upstream(ex, "recovery"); }
    }

    // ----------------------------------------------------- recovery/verify
    [AllowAnonymous]
    [PublicEndpoint("Recovery-code verify — proxies UM password/reset using the code as the reset token.")]
    [HttpPost("recovery/verify")]
    public async Task<IActionResult> RecoveryVerify([FromBody] RecoveryVerifyDto? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Code))
            return Problem(401, "invalid_recovery_code", "Invalid code", "The recovery code is missing.");

        // UM exposes no dedicated recovery-verify endpoint; the recovery code IS the
        // reset token UM issues. Echo it back as the resetToken so the set-password
        // step can present it. UM validates the token at password/reset time.
        return Ok(new { resetToken = body.Code, expiresInSeconds = 600 });
    }

    // ----------------------------------------------------------------- social
    [AllowAnonymous]
    [PublicEndpoint("Social login — proxies UM api/User/social, mints gateway session.")]
    [HttpPost("social")]
    public async Task<IActionResult> Social([FromBody] SocialDto? body, CancellationToken ct)
    {
        if (body is null
            || (string.IsNullOrWhiteSpace(body.SocialToken) && string.IsNullOrWhiteSpace(body.IdToken)))
            return Problem(400, "bad_request", "Missing token", "a social token is required.");

        try
        {
            var res = await _um.SocialAsync(new SocialLoginRequest
            {
                SocialId = body.SocialId,
                SocialToken = string.IsNullOrWhiteSpace(body.SocialToken) ? body.IdToken : body.SocialToken,
                SocialPlatform = string.IsNullOrWhiteSpace(body.SocialPlatform) ? body.Provider : body.SocialPlatform,
            }, ct);

            if (string.IsNullOrWhiteSpace(res?.UserId))
                return Problem(401, "invalid_credentials", "Social login failed", "The social token was rejected.");

            var (roles, active) = await ResolveRolesAsync(res!.UserId!, email: null, ct);
            await ProjectAsync(res.UserId!, roles, active, ct);
            var pair = await _tokens.IssueAsync(res.UserId!, roles, ct);
            _log.LogInformation("auth.social facade minted gateway session userId={UserId} recentlyCreated={Rc}",
                res.UserId, res.RecentlyCreated);

            // Social shape (authToken/refreshToken) PLUS the session-shape aliases so either
            // mobile consumer (social_auth_token.dart or _persistAndBuildSession) reads it.
            return Ok(new
            {
                userId = res.UserId,
                authToken = pair.AccessToken,
                accessToken = pair.AccessToken,
                refreshToken = pair.RefreshToken,
                recentlyCreated = res.RecentlyCreated,
                user = new { userId = res.UserId },
            });
        }
        catch (UmApiException ex) { return Upstream(ex, "social"); }
    }

    // ----------------------------------------------------------- helpers
    private async Task<IActionResult> MintSessionAsync(string? userId, string? email, string? status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Problem(401, "invalid_credentials", "Login failed", "No user was resolved.");

        var (roles, active) = await ResolveRolesAsync(userId!, email, ct);
        await ProjectAsync(userId!, roles, active, ct);
        var pair = await _tokens.IssueAsync(userId!, roles, ct);
        _log.LogInformation("auth.facade minted gateway session userId={UserId}", userId);

        return Ok(new
        {
            accessToken = pair.AccessToken,
            refreshToken = pair.RefreshToken,
            user = new { userId, id = userId, email, status },
        });
    }

    /// <summary>Resolve OPAQUE roles + active role from UM's persisted read; safe default 'customer'.</summary>
    private async Task<(IReadOnlyList<string> roles, string active)> ResolveRolesAsync(
        string userId, string? email, CancellationToken ct)
    {
        IReadOnlyList<string> roles = new[] { GwRoles.Client };
        var active = GwRoles.Client;
        try
        {
            var persisted = await _userManagement.GetUserRolesAsync(userId, ct);
            if (persisted is not null)
            {
                if (persisted.AvailableRoles is { Count: > 0 }) roles = persisted.AvailableRoles;
                if (!string.IsNullOrWhiteSpace(persisted.ActiveRole)) active = persisted.ActiveRole!;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "auth.facade UM get-roles failed for userId={UserId}; default role", userId);
        }

        // JEBV4-314 — union any DEV-seeded roles (POST /dev/seed/user role=admin) so an
        // email/password login of a seeded admin mints a JWT carrying the admin role. The
        // store is only ever populated by the [DevOnly] seed action, so in production this
        // resolves to null and the roles set is exactly UM's persisted read (no behaviour
        // change for real users). user-management stays authoritative for real identity.
        var seeded = _seededRoles.Resolve(userId, email);
        if (seeded is { Count: > 0 })
        {
            roles = roles.Union(seeded, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (!roles.Contains(active, StringComparer.OrdinalIgnoreCase))
            active = roles.Count > 0 ? roles[0] : GwRoles.Client;
        return (roles, active);
    }

    private Task ProjectAsync(string userId, IReadOnlyList<string> roles, string active, CancellationToken ct)
        => _users.UpsertProjectionAsync(new GwUserProfile
        {
            Id = userId,
            Phone = string.Empty,
            Name = string.Empty,
            Roles = roles.ToList(),
            ActiveRole = active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);

    private IActionResult Upstream(UmApiException ex, string leg)
    {
        // Map the UM upstream status to the mobile-contract codes WITHOUT echoing the body.
        _log.LogInformation("auth.facade.{Leg} upstream {Status}", leg, ex.StatusCode);
        return ex.StatusCode switch
        {
            401 => Problem(401, "invalid_credentials", "Unauthorized", "The credentials were rejected."),
            409 => Problem(409, "email_collision", "Conflict", "That email is already registered."),
            400 => Problem(400, "bad_request", "Bad request", "The request was invalid."),
            404 => Problem(404, "not_found", "Not found", "The user was not found."),
            _ => Problem(502, "upstream_error", "Upstream error", "The identity service is unavailable."),
        };
    }

    private ObjectResult Problem(int status, string code, string title, string detail)
        => OtpSignInProblems.Problem(this, status, code, title, detail);

    // ---- DTOs (mobile DioAuthRepository / social contract) ----
    public sealed class EmailLoginDto { public string? Email { get; set; } public string? Password { get; set; } }
    public sealed class EmailSignupDto { public string? Email { get; set; } public string? Password { get; set; } public string? Name { get; set; } }
    public sealed class SetPasswordDto { public string? Email { get; set; } public string? Password { get; set; } public string? ResetToken { get; set; } }
    public sealed class RecoveryDto { public string? Email { get; set; } }
    public sealed class RecoveryVerifyDto { public string? Email { get; set; } public string? Code { get; set; } }
    public sealed class SocialDto
    {
        public string? SocialId { get; set; }
        public string? SocialToken { get; set; }
        public string? SocialPlatform { get; set; }
        // mobile social_auth_service.dart aliases
        public string? IdToken { get; set; }
        public string? Provider { get; set; }
    }
}
