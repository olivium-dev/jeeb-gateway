using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Auth.SuperLogin;
using JeebGateway.service.ServiceUserManagement;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using GwUsersStore = JeebGateway.Users.IUsersStore;
using GwUserProfile = JeebGateway.Users.UserProfile;
using GwDualRoleClient = JeebGateway.Users.IUserManagementDualRoleClient;
using GwRoles = JeebGateway.Users.Roles;
using UserManagementApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Controllers
{
    /// <summary>
    /// Controller for managing user operations
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly ServiceUserManagementClient _serviceUserManagementClient;
        private readonly ITokenService _tokens;
        private readonly GwUsersStore _users;
        private readonly GwDualRoleClient _userManagement;
        private readonly IOptions<DemoUsersOptions> _demoUsers;
        private readonly IOptions<SuperLoginOptions> _superLogin;
        private readonly ILogger<UserController> _logger;

        public UserController(
            ServiceUserManagementClient serviceUserManagementClient,
            ITokenService tokens,
            GwUsersStore users,
            GwDualRoleClient userManagement,
            IOptions<DemoUsersOptions> demoUsers,
            IOptions<SuperLoginOptions> superLogin,
            ILogger<UserController> logger)
        {
            _serviceUserManagementClient = serviceUserManagementClient;
            _tokens = tokens;
            _users = users;
            _userManagement = userManagement;
            _demoUsers = demoUsers;
            _superLogin = superLogin;
            _logger = logger;
        }

        /// <summary>
        /// iter5 BATCHED-FIX — GET /api/User/demo-users. The debug-only
        /// Super-Login+ picker roster the mobile app
        /// (<c>DefaultSuperLoginDemoUserService</c>) lists. Anonymous by design
        /// (the picker precedes any session token), returns the frozen
        /// <c>{ users: [ { userId, name, role, passcode } ] }</c> shape. The roster
        /// + passcodes come ENTIRELY from configuration (<c>DemoUsers</c> section,
        /// supplied via env at deploy) — never hardcoded in source. Each row's
        /// passcode is the real SuperAdmin passcode the picker re-POSTs to
        /// <c>/api/User/user-id-login</c>, which user-management validates (the
        /// admin gate is unchanged — a wrong passcode still 401s there).
        /// </summary>
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Debug Super-Login+ picker roster — precedes any session token; ADR-005 §A public.")]
        [HttpGet("demo-users")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DemoUsers()
        {
            // SECURITY-GATE: the anonymous demo-user picker (rows carry the real
            // SuperAdmin passcode) is only served in DEMO open mode. Default
            // (OpenMode=false) = prod-safe → 404, so a flag-unset deploy never
            // exposes the roster anonymously. MSI demo env sets OpenMode=true.
            if (!_superLogin.Value.OpenMode)
            {
                return NotFound();
            }

            var opts = _demoUsers.Value;
            if (!opts.Enabled)
            {
                return NotFound();
            }

            var users = (opts.Users ?? new List<DemoUserRow>())
                .Where(u => !string.IsNullOrWhiteSpace(u.UserId)
                            && !string.IsNullOrWhiteSpace(u.Passcode))
                .Select(u => new
                {
                    userId = u.UserId,
                    name = string.IsNullOrWhiteSpace(u.Name) ? u.UserId : u.Name,
                    role = string.IsNullOrWhiteSpace(u.Role) ? "client" : u.Role,
                    passcode = u.Passcode,
                })
                .ToList();

            return Ok(new { users });
        }

        /// <summary>
        /// JEBV4-8 — GET /api/User/super-login/users. The FULL Super-Login+ picker
        /// roster: EVERY live user-management user (~84), not just the 3 seeded demo
        /// rows served by <c>/api/User/demo-users</c>. Sourced from user-management's
        /// own list API (<c>ServiceUserManagementClient.AllAsync</c> → UM
        /// <c>GET /api/User/all</c>) — the gateway NEVER reads UM's database directly
        /// (service boundary). Returns the demo-users-compatible
        /// <c>{ users: [ { userId, name, role, roles } ] }</c> shape but carries
        /// NO <c>passcode</c> field: real users never expose one. The picker re-POSTs
        /// its shared dev SuperAdmin passcode to <c>/api/User/user-id-login</c>, which
        /// user-management validates server-side (the admin gate is unchanged).
        ///
        /// SECURITY: this endpoint ENUMERATES all users. It is gated behind the SAME
        /// two flags as demo-users (<c>SuperLogin:OpenMode</c> + <c>DemoUsers:Enabled</c>),
        /// so the SEC-13 prod-off (OpenMode=false) kills it too → 404. It MUST die with
        /// SEC-13; never ship it enabled to production.
        /// </summary>
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Debug Super-Login+ FULL user picker roster — precedes any session token; gated by SuperLogin:OpenMode + DemoUsers:Enabled (dies with SEC-13). ADR-005 §A public.")]
        [HttpGet("super-login/users")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult> SuperLoginUsers()
        {
            // SECURITY-GATE: identical to demo-users. The anonymous full-roster picker
            // is only served in DEMO open mode. Default (OpenMode=false) or the
            // DemoUsers surface disabled = prod-safe → 404, so a flag-unset deploy
            // never enumerates users anonymously. Dies with SEC-13.
            if (!_superLogin.Value.OpenMode)
            {
                return NotFound();
            }
            if (!_demoUsers.Value.Enabled)
            {
                return NotFound();
            }

            // Source the FULL roster from user-management's own list API — page through
            // until hasMore is exhausted (cap the loop so a misbehaving upstream can't
            // spin us forever). NEVER touch UM's database directly (service boundary).
            const int pageSize = 200;
            const int maxPages = 100; // hard cap: 20k users
            var roster = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var skip = 0;
                for (var page = 0; page < maxPages; page++)
                {
                    var batch = await _serviceUserManagementClient.AllAsync(skip, pageSize, null);
                    var rows = batch?.Users;
                    if (rows is null || rows.Count == 0)
                    {
                        break;
                    }

                    foreach (var u in rows)
                    {
                        if (string.IsNullOrWhiteSpace(u.UserId) || !seen.Add(u.UserId!))
                        {
                            continue;
                        }

                        var available = (u.Available_roles ?? new List<string>())
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .ToList();
                        var role = !string.IsNullOrWhiteSpace(u.Active_role)
                            ? u.Active_role!
                            : (available.Count > 0 ? available[0] : "client");

                        // NO passcode field for real users — the picker submits the
                        // shared dev SuperAdmin passcode, which UM validates server-side.
                        roster.Add(new
                        {
                            userId = u.UserId,
                            name = string.IsNullOrWhiteSpace(u.Username) ? u.UserId : u.Username,
                            role,
                            roles = available,
                        });
                    }

                    if (batch is not null && !batch.HasMore)
                    {
                        break;
                    }
                    skip += pageSize;
                }
            }
            catch (UserManagementApiException ex)
            {
                _logger.LogError(ex, "user.super-login/users UM list failed (status={Status})", ex.StatusCode);
                return Problem(statusCode: StatusCodes.Status502BadGateway,
                    detail: "Failed to load the user roster from user-management.",
                    title: "Bad Gateway");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "user.super-login/users unexpected failure loading roster");
                return Problem(statusCode: StatusCodes.Status502BadGateway,
                    detail: "Failed to load the user roster from user-management.",
                    title: "Bad Gateway");
            }

            return Ok(new { users = roster });
        }

        /// <summary>
        /// JEBV4-249 (residual of JEBV4-63) — map a caught upstream user-management
        /// <see cref="UserManagementApiException"/> to a sanitized RFC 7807 ProblemDetails.
        /// The upstream status is preserved (clamped to a valid 4xx/5xx; anything else →
        /// 502 Bad Gateway), but the upstream exception message / response body is NEVER
        /// echoed to the caller — it is logged server-side only. The prior JEBV4-63 partial
        /// fix wrapped the leak in an RFC 7807 envelope but still forwarded the raw upstream
        /// <c>ex.Message</c> as the response <c>detail</c>; that information-disclosure leak
        /// is removed here. Mirrors the JEBV4-242 <c>ChatController.UpstreamProblem</c> idiom.
        /// </summary>
        private ActionResult UpstreamProblem(UserManagementApiException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            _logger.LogWarning(ex,
                "User BFF: user-management call failed on {Method} {Path} → {Status}.",
                Request.Method, Request.Path, status);

            return Problem(
                statusCode: status,
                title: status == StatusCodes.Status404NotFound
                    ? "Not Found"
                    : "Upstream user-management error");
        }

        private ActionResult<(string userId, bool isValid)> ValidateUserAndServices()
        {
            var userId = User.FindFirst(ClaimTypes.Sid)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                userId = User.FindFirst("sid")?.Value;
            }
            if (string.IsNullOrEmpty(userId))
            {
                userId = User.FindFirst("sub")?.Value;
            }
            if (string.IsNullOrEmpty(userId))
            {
                throw new UserManagementApiException("Unauthorized: User ID not found in token", 401, "Unauthorized", new Dictionary<string, IEnumerable<string>>(), null);
            }

            if (_serviceUserManagementClient == null)
            {
                throw new UserManagementApiException("Error: ServiceUserManagementClient is not initialized", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }

            return (userId, true);
        }

        /// <summary>
        /// Health check endpoint
        /// </summary>
        /// <returns>Success response</returns>
        /// <response code="200">Service is healthy</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("check")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileReadSelf)] // ADR-005 §B self / any-auth
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Check()
        {
            try
            {
                await _serviceUserManagementClient.CheckAsync();
                return Ok();
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Get all users
        /// </summary>
        /// <param name="skip">Number of users to skip</param>
        /// <param name="limit">Maximum number of users to return</param>
        /// <param name="onActive">Filter by active status</param>
        /// <returns>List of users</returns>
        /// <response code="200">Users retrieved successfully</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("all")]
        // ADR-005 §M admin/internal: bulk user listing is an admin op (users.admin.manage). Carried only
        // the L1 fallback today; declaring {admin} is the documented ADR posture (reachability triage M).
        [RequireCapability(Capabilities.UsersAdminManage)]
        [ProducesResponseType(typeof(GetAllUsersResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<GetAllUsersResponse>> GetAllUsers([FromQuery] int? skip = null, [FromQuery] int? limit = null, [FromQuery] bool? onActive = null)
        {
            try
            {
                var response = await _serviceUserManagementClient.AllAsync(skip, limit, onActive);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Register a new user
        /// </summary>
        /// <param name="request">User registration request</param>
        /// <returns>Registration response</returns>
        /// <response code="201">User registered successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — registration precedes any session token.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Registration precedes any session token — ADR-005 §M/§A public.")]
        [HttpPost("register")]
        [ProducesResponseType(typeof(RegisterUserResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<RegisterUserResponse>> Register([FromBody] RegisterUserRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.RegisterAsync(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// User login
        /// </summary>
        /// <param name="request">Login request</param>
        /// <returns>Login response with token</returns>
        /// <response code="200">Login successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — email+password login mints the session token.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Email+password login mints the session token — ADR-005 §M/§A public.")]
        [HttpPost("login")]
        [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.LoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Token-based login
        /// </summary>
        /// <param name="request">Token login request</param>
        /// <returns>Login response</returns>
        /// <response code="200">Token login successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — token-login exchanges a login token for a session.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Token-login exchanges a login token for a session — ADR-005 §M/§A public.")]
        [HttpPost("token-login")]
        [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<LoginResponse>> TokenLogin([FromBody] TokenLoginRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.TokenLoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        // ADR-004 D1: public by design — userId login entry (gated by its own credential).
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("userId login entry (own credential) — ADR-005 §M/§A public.")]
        [HttpPost("login/userId")]
        [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<LoginResponse>> LoginByUserId([FromBody] UserIdLoginRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.UserIdLoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// User ID based login
        /// </summary>
        /// <param name="request">User ID login request</param>
        /// <returns>Social login response</returns>
        /// <response code="200">User ID login successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — super-login entry, gated by its own SuperAdminPassCode.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Super-login entry, gated by SuperAdminPassCode — ADR-005 §M/§A public.")]
        [HttpPost("user-id-login")]
        [HttpPost("userid-login")]
        [ProducesResponseType(typeof(SocialLoginResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SocialLoginResponse>> UserIdLogin([FromBody] UserIdLoginRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                // GATE INTACT: user-management validates the SuperAdmin passcode. A wrong
                // passcode (e.g. 123768) throws a 401 UserManagementApiException BEFORE we
                // ever mint, so the privileged gate is unchanged and not weakened here.
                var response = await _serviceUserManagementClient.UserIdLoginAsync(request);

                // iter5 superlogin fix — UM returns a token with iss/aud=user-management,
                // which the gateway /v1/* routes (aud=jeeb-clients) reject (401 "audience
                // 'user-management' is invalid"). Re-mint a REAL gateway SESSION token using
                // the SAME ITokenService.IssueAsync the OTP-verify path uses, so the issued
                // accessToken is identical in kind to an OTP session (iss=jeeb-gateway,
                // aud=jeeb-clients, sub=userId, roles + active_role, exp). UM stays the
                // identity + admin-gate authority; the session JWT mint is gateway
                // orchestration (same split as OTP verify and the /auth/tokens super-login+).
                var userId = response?.UserId;
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var (opaqueRoles, opaqueActiveRole) = await ResolveSuperLoginRolesAsync(userId!);

                    // Project the UM-resolved identity locally so the gateway-minted JWT embeds
                    // the SAME active_role/roles claims (TokenService reads active_role from the
                    // store) — mirrors the OTP verify path's UpsertProjectionAsync.
                    await _users.UpsertProjectionAsync(new GwUserProfile
                    {
                        Id = userId!,
                        Phone = string.Empty,
                        Name = string.Empty,
                        Roles = opaqueRoles.ToList(),
                        ActiveRole = opaqueActiveRole,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }, HttpContext.RequestAborted);

                    var pair = await _tokens.IssueAsync(userId!, opaqueRoles, HttpContext.RequestAborted);

                    // Swap UM's user-management-audience token for the gateway session token.
                    response!.AuthToken = pair.AccessToken;
                    response!.RefreshToken = pair.RefreshToken;

                    _logger.LogInformation("user.user-id-login super-login re-minted gateway session userId={UserId}", userId);
                }

                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// iter5 super-login fix. Resolves the OPAQUE ({customer,driver}) role set + active
        /// role for a super-login userId from user-management's persisted role read, mirroring
        /// the OTP verify path's <c>SafeGetUserRolesAsync</c>. Best-effort: a UM 404/blip
        /// degrades to the safe default ('customer') so super-login is never hard-broken by a
        /// UM read failure (the session is still gateway-minted). Never emits an active_role the
        /// user does not hold (token integrity).
        /// </summary>
        private async Task<(IReadOnlyList<string> roles, string activeRole)> ResolveSuperLoginRolesAsync(string userId)
        {
            IReadOnlyList<string> roles = new[] { GwRoles.Client };
            var active = GwRoles.Client;
            try
            {
                var persisted = await _userManagement.GetUserRolesAsync(userId, HttpContext.RequestAborted);
                if (persisted is not null)
                {
                    if (persisted.AvailableRoles is { Count: > 0 })
                        roles = persisted.AvailableRoles;
                    if (!string.IsNullOrWhiteSpace(persisted.ActiveRole))
                        active = persisted.ActiveRole!;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "user.user-id-login UM get-roles read failed for userId={UserId}; using default role decoration",
                    userId);
            }

            if (!roles.Contains(active, StringComparer.OrdinalIgnoreCase))
            {
                active = roles.Count > 0 ? roles[0] : GwRoles.Client;
            }

            return (roles, active);
        }

        /// <summary>
        /// User logout
        /// </summary>
        /// <param name="request">Logout request</param>
        /// <returns>Logout response</returns>
        /// <response code="200">Logout successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — logout operates on a supplied userId/token, no session gate.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Logout on a supplied userId/token (no session gate) — ADR-005 §M/§A public.")]
        [HttpPost("logout")]
        [ProducesResponseType(typeof(LogoutResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<LogoutResponse>> Logout([FromBody] LogoutRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.LogoutAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Social login
        /// </summary>
        /// <param name="request">Social login request</param>
        /// <returns>Social login response</returns>
        /// <response code="200">Social login successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        // ADR-004 D1: public by design — social SSO login mints the session token.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [PublicEndpoint("Social login mints the session token — ADR-005 §M/§A public.")]
        [HttpPost("social")]
        [ProducesResponseType(typeof(SocialLoginResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SocialLoginResponse>> SocialLogin([FromBody] SocialLoginRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.SocialAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Forgot password
        /// </summary>
        /// <param name="request">Forgot password request</param>
        /// <returns>Forgot password response</returns>
        /// <response code="200">Password reset email sent</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("forgot")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self credential mgmt
        [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.ForgotAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Reset password
        /// </summary>
        /// <param name="request">Reset password request</param>
        /// <returns>Reset password response</returns>
        /// <response code="200">Password reset successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("reset")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self credential mgmt
        [ProducesResponseType(typeof(ResetPasswordResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ResetPasswordResponse>> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.ResetAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpGet("profile/{userId}")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileReadSelf)] // ADR-005 §B self / any-auth (scoping = STATE)
        [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UserProfileResponse>> ProfileById(string userId)
        {
            try
            {
                var response = await _serviceUserManagementClient.ProfileAsync(userId);

                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Get user profile
        /// </summary>
        /// <param name="userId">User ID (optional, uses authenticated user if not provided)</param>
        /// <returns>User profile</returns>
        /// <response code="200">Profile retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">User not found</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("profile")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileReadSelf)] // ADR-005 §B self / any-auth (scoping = STATE)
        [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UserProfileResponse>> GetProfile([FromQuery] string? userId = null)
        {
            try
            {
                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var targetUserId = string.IsNullOrEmpty(userId) ? validationResult.Value.userId : userId;
                var response = await _serviceUserManagementClient.ProfileAsync(targetUserId);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Update user profile
        /// </summary>
        /// <param name="request">Update profile request</param>
        /// <returns>Update response</returns>
        /// <response code="200">Profile updated successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        [HttpPut("profile")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self (ownership = STATE)
        [ProducesResponseType(typeof(UpdateUserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UpdateUserProfileResponse>> UpdateProfile([FromBody] UpdateUserProfileRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.UpdateAsync(request);

                // jeeberName gap fix: mirror the updated display fields into the
                // gateway's local users projection (best-effort, never fails the 200).
                await MirrorProfileUpdateToLocalProjectionAsync(request, response);

                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPut("profile/update")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self (ownership = STATE)
        [ProducesResponseType(typeof(UpdateUserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UpdateUserProfileResponse>> UpdateProfileViaRoute([FromBody] UpdateUserProfileRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.UpdateAsync(request);

                // jeeberName gap fix: same local-projection mirror as PUT /profile.
                await MirrorProfileUpdateToLocalProjectionAsync(request, response);

                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// jeeberName gap fix (feat/tier-unify-names lane). ROOT CAUSE: the deliveries
        /// GetById jeeberName enrichment reads the gateway's LOCAL users projection
        /// (<see cref="JeebGateway.Users.IUsersStore"/>), but NOTHING ever wrote a
        /// display name into it — the OTP-verify projection upsert carries Name = ""
        /// (user-management's phone find-or-create is identity-only), and this profile
        /// proxy previously forwarded <c>username</c> to user-management WITHOUT
        /// mirroring it locally. So real (OTP-minted) accounts always enriched to an
        /// empty name.
        ///
        /// <para>This mirror lands the upstream-accepted display fields (username →
        /// Name, profilePic → AvatarUrl, email → Email) in the local projection, keyed
        /// by the upstream-confirmed user id (falling back to the request body's id,
        /// then the caller's own claims). BEST-EFFORT: the upstream update already
        /// succeeded, so a local mirror fault only logs — it never flips the 200.</para>
        /// </summary>
        private async Task MirrorProfileUpdateToLocalProjectionAsync(
            UpdateUserProfileRequest request, UpdateUserProfileResponse? response)
        {
            try
            {
                var userId = response?.UserId;
                if (string.IsNullOrWhiteSpace(userId)) userId = request.UserId;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    userId = User.FindFirst(ClaimTypes.Sid)?.Value
                             ?? User.FindFirst("sid")?.Value
                             ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                             ?? User.FindFirst("sub")?.Value;
                }
                if (string.IsNullOrWhiteSpace(userId)) return;

                // Prefer the upstream-echoed values (what UM actually persisted) and
                // fall back to the submitted ones. Null patch fields are left untouched
                // by the store, so a partial update never blanks the other fields.
                var name = FirstNonBlank(response?.Username, request.Username);
                var avatar = FirstNonBlank(response?.ProfilePic, request.ProfilePic);
                var email = FirstNonBlank(response?.Email, request.Email);
                if (name is null && avatar is null && email is null) return;

                await _users.UpdateProfileAsync(userId!, new JeebGateway.Users.ProfilePatch
                {
                    Name = name,
                    AvatarUrl = avatar,
                    Email = email,
                }, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "user.profile update local-projection mirror failed; upstream update already succeeded.");
            }
        }

        private static string? FirstNonBlank(string? preferred, string? fallback)
            => !string.IsNullOrWhiteSpace(preferred) ? preferred
             : !string.IsNullOrWhiteSpace(fallback) ? fallback
             : null;

        /// <summary>
        /// Delete user profile
        /// </summary>
        /// <param name="userId">User ID (optional, uses authenticated user if not provided)</param>
        /// <returns>Delete response</returns>
        /// <response code="200">Profile deleted successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">User not found</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("profile")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self (ownership = STATE)
        [ProducesResponseType(typeof(DeleteUserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DeleteUserProfileResponse>> DeleteProfile([FromQuery] string? userId = null)
        {
            try
            {
                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var callerId = validationResult.Value.userId;
                var targetUserId = string.IsNullOrEmpty(userId) ? callerId : userId;

                // SEC-IDOR (Leg-11): this is the SELF profile-delete (ProfileWriteSelf =
                // any-authenticated). Before the guard, the query-param userId won over the
                // caller's token id with NO ownership check, so any authenticated caller could
                // permanently delete ANY account (BOLA). Enforce caller==target; genuine admin
                // deletion of another account goes through the admin-capability bulk endpoint.
                if (!string.Equals(targetUserId, callerId, StringComparison.Ordinal)
                    && !JeebGateway.Users.UserIdentity.IsAdmin(HttpContext))
                {
                    var ownershipProblem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Type = "https://jeeb.dev/errors/forbidden-ownership",
                        Title = "Forbidden",
                        Detail = "You may only delete your own account.",
                        Status = StatusCodes.Status403Forbidden,
                        Instance = HttpContext.Request.Path
                    };
                    // Emit the RFC7807 application/problem+json shape explicitly (matching
                    // CapabilityForbiddenResultHandler); ControllerBase.Problem()/ObjectResult
                    // content-negotiate down to application/json in this app.
                    return new ContentResult
                    {
                        StatusCode = StatusCodes.Status403Forbidden,
                        ContentType = "application/problem+json",
                        Content = System.Text.Json.JsonSerializer.Serialize(
                            ownershipProblem,
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                    };
                }

                var response = await _serviceUserManagementClient.DeleteAsync(targetUserId);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpDelete("profile/delete")]
        [Authorize]
        [RequireCapability(Capabilities.ProfileWriteSelf)] // ADR-005 §B self (ownership = STATE)
        [ProducesResponseType(typeof(DeleteUserProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DeleteUserProfileResponse>> DeleteProfileByToken()
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.Sid)?.Value;
                if (string.IsNullOrEmpty(userId))
                {
                    userId = User.FindFirst("sid")?.Value;
                }
                if (string.IsNullOrEmpty(userId))
                {
                    userId = User.FindFirst("sub")?.Value;
                }
                if (string.IsNullOrEmpty(userId))
                {
                    // JEBV4-63: was Unauthorized("User ID not found in token.") — a bare
                    // string body. Empty Unauthorized() is upgraded to RFC7807 by the
                    // gateway's status-code pages.
                    return Unauthorized();
                }

                var response = await _serviceUserManagementClient.DeleteAsync(userId);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Delete users by email addresses (bulk operation)
        /// </summary>
        /// <param name="request">Bulk email request</param>
        /// <returns>Bulk operation response</returns>
        /// <response code="200">Users deleted successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("bulk/emails")]
        // ADR-005 §M admin/internal: bulk delete-by-emails is an admin op (users.admin.manage).
        [RequireCapability(Capabilities.UsersAdminManage)]
        [ProducesResponseType(typeof(BulkOperationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<BulkOperationResponse>> DeleteByEmails([FromBody] BulkEmailRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.DeleteByEmailsAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Register device for push notifications
        /// </summary>
        /// <param name="request">Device registration request</param>
        /// <returns>Registration response</returns>
        /// <response code="201">Device registered successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("device/register")]
        // ADR-005 §M: device-token registration carries no user-type gate today (L1 fallback only);
        // L2-public preserves that. Behaviour-preserving.
        [PublicEndpoint("Device-token register — L1-only today; ADR-005 §M L2-public (behaviour-preserving).")]
        [ProducesResponseType(typeof(RegisterDeviceResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<RegisterDeviceResponse>> RegisterDevice([FromBody] RegisterDeviceRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.Register2Async(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Unregister device from push notifications
        /// </summary>
        /// <param name="request">Device unregistration request</param>
        /// <returns>Unregistration response</returns>
        /// <response code="200">Device unregistered successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("device/unregister")]
        // ADR-005 §M: device-token unregistration carries no user-type gate today (L1 fallback only).
        [PublicEndpoint("Device-token unregister — L1-only today; ADR-005 §M L2-public (behaviour-preserving).")]
        [ProducesResponseType(typeof(UnregisterDeviceResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UnregisterDeviceResponse>> UnregisterDevice([FromBody] UnregisterDeviceRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.UnregisterAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Issue payment authentication token
        /// </summary>
        /// <param name="request">Payment auth token request</param>
        /// <returns>Payment auth token response</returns>
        /// <response code="200">Token issued successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("payment/auth-token")]
        // ADR-005 §M admin/internal: minting a payment auth-token is a privileged op (users.admin.manage).
        [RequireCapability(Capabilities.UsersAdminManage)]
        [ProducesResponseType(typeof(PaymentAuthTokenResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<PaymentAuthTokenResponse>> IssuePaymentAuthToken([FromBody] PaymentAuthTokenRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.IssuePaymentAuthTokenAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("issue-payment-auth-token")]
        [Authorize]
        // ADR-005 §M admin/internal: payment auth-token issuance is privileged (users.admin.manage).
        [RequireCapability(Capabilities.UsersAdminManage)]
        [ProducesResponseType(typeof(PaymentAuthTokenResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> IssuePaymentAuthToken([FromHeader(Name = "Authorization")] string authorizationHeader, [FromBody] PaymentAuthTokenRequest request)
        {
            try
            {
                var token = authorizationHeader.Substring("Bearer ".Length).Trim();
                request.Token = token;

                var response = await _serviceUserManagementClient.IssuePaymentAuthTokenAsync(request);

                Response.Cookies.Append("authToken", response.AuthToken, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTime.Now.AddMinutes(2)
                });

                return Ok("Token issued and cookie set.");
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        /// <summary>
        /// Validate payment authentication token
        /// </summary>
        /// <param name="request">Validate payment auth token request</param>
        /// <returns>Validation response</returns>
        /// <response code="200">Token validated successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Invalid token</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("payment/validate-auth-token")]
        // ADR-005 §M: payment auth-token validation is an internal verification with no user-type gate
        // today (L1 fallback only); L2-public preserves that. Behaviour-preserving.
        [PublicEndpoint("Payment auth-token validation — internal/L1-only; ADR-005 §M L2-public.")]
        [ProducesResponseType(typeof(ValidatePaymentAuthTokenResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ValidatePaymentAuthTokenResponse>> ValidatePaymentAuthToken([FromBody] ValidatePaymentAuthTokenRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Title = "Request body is required.",
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://jeeb.dev/errors/request-body-required"
                    });
                }

                var response = await _serviceUserManagementClient.ValidatePaymentAuthTokenAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }
    }
}