using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using RemoteUserPreferencesApiException = JeebGateway.Services.Generated.ServiceRemoteUserPreferences.ApiException;

namespace JeebGateway.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    // ADR-005 L2 §B self / any-authenticated: ALL UserPreferences actions are the caller's own
    // preferences (notification.prefs.self), class-level. L1 [Authorize] preserved above.
    [RequireCapability(Capabilities.NotificationPrefsSelf)]
    public class UserPreferencesController : ControllerBase
    {
        private const string PrefKeyFullName = "fullName";
        private const string PrefKeyAddress = "address";

        private readonly ServiceRemoteUserPreferencesClient _preferencesClient;
        private readonly ILogger<UserPreferencesController> _logger;

        public UserPreferencesController(
            ServiceRemoteUserPreferencesClient preferencesClient,
            ILogger<UserPreferencesController> logger)
        {
            _preferencesClient = preferencesClient;
            _logger = logger;
        }

        /// <summary>
        /// JEBV4-249 (residual of JEBV4-63) — map a caught upstream user-preferences
        /// <see cref="RemoteUserPreferencesApiException"/> to a sanitized RFC 7807
        /// ProblemDetails. The upstream status is preserved (clamped to a valid 4xx/5xx;
        /// anything else → 502 Bad Gateway), but the upstream message / response body is
        /// NEVER echoed to the caller — it is logged server-side only. The prior JEBV4-63
        /// partial fix wrapped the leak in an envelope but still forwarded the raw upstream
        /// <c>ex.Message</c> as the response detail; that information-disclosure leak is
        /// removed here. Mirrors the JEBV4-242 ChatController.UpstreamProblem idiom.
        /// </summary>
        private IActionResult UpstreamProblem(RemoteUserPreferencesApiException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            _logger.LogWarning(ex,
                "UserPreferences BFF: user-preferences call failed on {Method} {Path} → {Status}.",
                Request.Method, Request.Path, status);

            return Problem(
                title: "Upstream user-preferences error",
                statusCode: status);
        }

        private string? GetUserId()
        {
            // The gateway mints access tokens with the user id in the JWT `sub`
            // claim (TokenService.BuildAccessToken) and runs with
            // MapInboundClaims = false + NameClaimType = "sub" (Program.cs), so the
            // user id is NOT surfaced as ClaimTypes.Sid. Reading only Sid here
            // returned null and 401'd every authenticated /api/UserPreferences/*
            // call against a minted token. Fall back through the claim aliases a
            // gateway-minted or upstream token may carry — matching the resolution
            // order NotificationController already uses — so a valid token
            // authorizes the existing DB-backed preference routes.
            return User.FindFirst(ClaimTypes.Sid)?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sid")?.Value;
        }

        #region Data Set Operations

        [HttpGet("data/{data_type}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPaginatedItems(
            string data_type,
            [FromQuery] int? page,
            [FromQuery] int? size,
            [FromQuery] string? order)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var result = await _preferencesClient.Data_GetItemsAsync(userId, data_type, page, size, order);
                return Ok(result);
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("data/{data_type}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddItemsToSet(
            string data_type,
            [FromBody] IEnumerable<string> items)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_AddItemsAsync(userId, data_type, items);
                return StatusCode(201, new { Message = "Items added successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPut("data/{data_type}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ReplaceSet(
            string data_type,
            [FromBody] IEnumerable<string> items)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_ReplaceSetAsync(userId, data_type, items);
                return Ok(new { Message = "Set replaced successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpDelete("data/{data_type}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteFromSet(
            string data_type,
            [FromBody] IEnumerable<string> items)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_DeleteFromSetAsync(userId, data_type, items);
                return Ok(new { Message = "Items deleted successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpGet("data/{data_type}/all")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetAllItems(string data_type)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var result = await _preferencesClient.Data_GetAllItemsAsync(userId, data_type);
                return Ok(result);
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpDelete("data/{data_type}/all")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteEntireSet(string data_type)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_DeleteSetAsync(userId, data_type);
                return Ok(new { Message = "Set deleted successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        #endregion

        #region Personal Information

        #endregion

        #region Nested Preferences

        [HttpGet("nested-preferences/{pref_key}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetNestedPreference(string pref_key)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var result = await _preferencesClient.Data_GetNestedPreferenceAsync(userId, pref_key);
                return Ok(result);
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("nested-preferences/{pref_key}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SetNestedPreference(
            string pref_key,
            [FromBody] NestedPreferenceInput preference)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_SetNestedPreferenceAsync(userId, pref_key, preference);
                return StatusCode(201, new { Message = "Nested preference set successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        #endregion

        #region Preferences

        [HttpGet("preferences")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetAllPreferences()
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var result = await _preferencesClient.Data_GetPreferencesAsync(userId);
                return Ok(result);
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("preferences")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SetPreference([FromBody] PreferenceInput preference)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_SetPreferenceAsync(userId, preference);
                return StatusCode(201, new { Message = "Preference set successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpGet("preferences/{pref_key}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetSinglePreference(string pref_key)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var result = await _preferencesClient.Data_GetSinglePreferenceAsync(userId, pref_key);
                return Ok(result);
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("preferences/{pref_key}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SetSinglePreference(
            string pref_key,
            [FromBody] PreferenceValue value)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_SetSinglePreferenceAsync(userId, pref_key, value);
                return StatusCode(201, new { Message = "Preference set successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPut("preferences/{pref_key}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UpdatePreference(
            string pref_key,
            [FromBody] PreferenceValue value)
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                await _preferencesClient.Data_UpdatePreferenceAsync(userId, pref_key, value);
                return Ok(new { Message = "Preference updated successfully" });
            }
            catch (RemoteUserPreferencesApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        #endregion
    }
}
