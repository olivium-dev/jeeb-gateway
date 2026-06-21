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
using JeebGateway.service.ServiceUserManagement;
using JeebGateway.Tokens;
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
    [Produces("application/json")]
    public class UserController : ControllerBase
    {
        private readonly ServiceUserManagementClient _serviceUserManagementClient;
        private readonly ITokenService _tokens;
        private readonly GwUsersStore _users;
        private readonly GwDualRoleClient _userManagement;
        private readonly ILogger<UserController> _logger;

        public UserController(
            ServiceUserManagementClient serviceUserManagementClient,
            ITokenService tokens,
            GwUsersStore users,
            GwDualRoleClient userManagement,
            ILogger<UserController> logger)
        {
            _serviceUserManagementClient = serviceUserManagementClient;
            _tokens = tokens;
            _users = users;
            _userManagement = userManagement;
            _logger = logger;
        }

        private ActionResult HandleUpstreamException(UserManagementApiException ex)
        {
            if (ex.StatusCode == 404)
                return Problem(statusCode: 404, detail: ex.Message, title: "Not Found");
            return StatusCode(ex.StatusCode, ex.Message);
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check");
                return StatusCode(500, $"Error in health check: {ex.Message}");
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all users");
                return StatusCode(500, $"Error retrieving all users: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.RegisterAsync(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering user");
                return StatusCode(500, $"Error registering user: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.LoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during login");
                return StatusCode(500, $"Error during login: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.TokenLoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token login");
                return StatusCode(500, $"Error during token login: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.UserIdLoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during login by userId");
                return StatusCode(500, $"Error during login by userId: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during user ID login");
                return StatusCode(500, $"Error during user ID login: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.LogoutAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during logout");
                return StatusCode(500, $"Error during logout: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.SocialAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during social login");
                return StatusCode(500, $"Error during social login: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.ForgotAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during forgot password");
                return StatusCode(500, $"Error during forgot password: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.ResetAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during password reset");
                return StatusCode(500, $"Error during password reset: {ex.Message}");
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user profile");
                return StatusCode(500, $"Error retrieving user profile: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.UpdateAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user profile");
                return StatusCode(500, $"Error updating user profile: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.UpdateAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user profile");
                return StatusCode(500, $"Error updating user profile: {ex.Message}");
            }
        }

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

                var targetUserId = string.IsNullOrEmpty(userId) ? validationResult.Value.userId : userId;
                var response = await _serviceUserManagementClient.DeleteAsync(targetUserId);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user profile");
                return StatusCode(500, $"Error deleting user profile: {ex.Message}");
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
                    return Unauthorized("User ID not found in token.");
                }

                var response = await _serviceUserManagementClient.DeleteAsync(userId);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user profile");
                return StatusCode(500, $"Error deleting user profile: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.DeleteByEmailsAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting users by emails");
                return StatusCode(500, $"Error deleting users by emails: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.Register2Async(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering device");
                return StatusCode(500, $"Error registering device: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.UnregisterAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unregistering device");
                return StatusCode(500, $"Error unregistering device: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.IssuePaymentAuthTokenAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing payment auth token");
                return StatusCode(500, $"Error issuing payment auth token: {ex.Message}");
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
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                    return BadRequest("Request body cannot be null");
                }

                var response = await _serviceUserManagementClient.ValidatePaymentAuthTokenAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return HandleUpstreamException(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating payment auth token");
                return StatusCode(500, $"Error validating payment auth token: {ex.Message}");
            }
        }
    }
}