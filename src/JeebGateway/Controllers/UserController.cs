using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.service.ServiceUserManagement;
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
        private readonly ILogger<UserController> _logger;

        public UserController(
            ServiceUserManagementClient serviceUserManagementClient,
            ILogger<UserController> logger)
        {
            _serviceUserManagementClient = serviceUserManagementClient;
            _logger = logger;
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token login");
                return StatusCode(500, $"Error during token login: {ex.Message}");
            }
        }

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
                return StatusCode(ex.StatusCode, ex.Message);
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

                var response = await _serviceUserManagementClient.UserIdLoginAsync(request);
                return Ok(response);
            }
            catch (UserManagementApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during user ID login");
                return StatusCode(500, $"Error during user ID login: {ex.Message}");
            }
        }

        /// <summary>
        /// User logout
        /// </summary>
        /// <param name="request">Logout request</param>
        /// <returns>Logout response</returns>
        /// <response code="200">Logout successful</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during password reset");
                return StatusCode(500, $"Error during password reset: {ex.Message}");
            }
        }

        [HttpGet("profile/{userId}")]
        [Authorize]
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user profile");
                return StatusCode(500, $"Error updating user profile: {ex.Message}");
            }
        }

        [HttpPut("profile/update")]
        [Authorize]
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user profile");
                return StatusCode(500, $"Error deleting user profile: {ex.Message}");
            }
        }

        [HttpDelete("profile/delete")]
        [Authorize]
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing payment auth token");
                return StatusCode(500, $"Error issuing payment auth token: {ex.Message}");
            }
        }

        [HttpPost("issue-payment-auth-token")]
        [Authorize]
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
                return StatusCode(ex.StatusCode, ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating payment auth token");
                return StatusCode(500, $"Error validating payment auth token: {ex.Message}");
            }
        }
    }
}