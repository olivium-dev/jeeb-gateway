using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.DTOs.PushNotification;
using JeebGateway.service.ServicePushNotification;
using PushNotificationApiException = JeebGateway.service.ServicePushNotification.ApiException;

namespace JeebGateway.Controllers
{
    /// <summary>
    /// Controller for managing push notifications and device registrations
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class PushNotificationController : ControllerBase
    {
        private readonly ServicePushNotificationClient _servicePushNotificationClient;
        private readonly ILogger<PushNotificationController> _logger;

        public PushNotificationController(
            ServicePushNotificationClient servicePushNotificationClient,
            ILogger<PushNotificationController> logger)
        {
            _servicePushNotificationClient = servicePushNotificationClient;
            _logger = logger;
        }

        private ActionResult<(string userId, bool isValid)> ValidateUserAndServices()
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value;
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
                throw new PushNotificationApiException("Unauthorized: User ID not found in token", 401, "Unauthorized", new Dictionary<string, IEnumerable<string>>(), null);
            }

            if (_servicePushNotificationClient == null)
            {
                throw new PushNotificationApiException("Error: ServicePushNotificationClient is not initialized", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }

            return (userId, true);
        }

        /// <summary>
        /// Register a device for push notifications
        /// </summary>
        /// <remarks>
        /// Register a device for push notifications. The user ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="request">Device registration request</param>
        /// <returns>Registration response</returns>
        /// <response code="201">Device registered successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpPut("register")]
        [Authorize]
        [RequireCapability(Capabilities.NotificationPrefsSelf)] // ADR-005 §B self device-registration
        [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<RegisterResponse>> RegisterDevice([FromBody] RegisterDeviceRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new PushNotificationApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var serviceRequest = new RegisterRequest
                {
                    User_id = userId,
                    Fcm_token = request.FcmToken,
                    Device_id = request.DeviceId
                };

                var response = await _servicePushNotificationClient.Register_deviceAsync(serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error registering device: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Delete a device registration for a user
        /// </summary>
        /// <remarks>
        /// Delete a specific device registration for the authenticated user. The user ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="request">Device deletion request</param>
        /// <returns>Deletion response</returns>
        /// <response code="201">Device deleted successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("device")]
        [Authorize]
        [RequireCapability(Capabilities.NotificationPrefsSelf)] // ADR-005 §B self device-management
        [ProducesResponseType(typeof(DeleteByDeviceAndUserResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DeleteByDeviceAndUserResponse>> DeleteDevice([FromBody] DeleteDeviceRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new PushNotificationApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var serviceRequest = new DeleteByDeviceAndUserRequest
                {
                    User_id = userId,
                    Device_id = request.DeviceId
                };

                var response = await _servicePushNotificationClient.Delete_device_by_device_and_userAsync(serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error deleting device: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Delete all device registrations for a user
        /// </summary>
        /// <remarks>
        /// Delete all device registrations for the authenticated user. The user ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <returns>Deletion response</returns>
        /// <response code="201">All devices deleted successfully</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("devices")]
        [Authorize]
        [RequireCapability(Capabilities.NotificationPrefsSelf)] // ADR-005 §B self device-management
        [ProducesResponseType(typeof(DeleteByUserResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DeleteByUserResponse>> DeleteAllDevices()
        {
            try
            {
                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;
                var serviceRequest = new DeleteByUserRequest
                {
                    User_id = userId
                };

                var response = await _servicePushNotificationClient.Delete_all_devices_by_userAsync(serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error deleting all devices: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Send a notification to a specific device
        /// </summary>
        /// <remarks>
        /// Send a notification to a specific device. The user ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="deviceId">Device ID</param>
        /// <param name="request">Notification request</param>
        /// <returns>Notification response</returns>
        /// <response code="201">Notification sent successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("device/{deviceId}")]
        [Authorize]
        [RequireCapability(Capabilities.NotificationPrefsSelf)] // ADR-005 §B self device-targeted send
        [ProducesResponseType(typeof(SentPayloadResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SentPayloadResponse>> SendNotificationToDevice(
            [FromRoute] string deviceId,
            [FromBody] SendNotificationToDeviceRequestDto request)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    throw new PushNotificationApiException("DeviceId is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new PushNotificationApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var serviceRequest = new SentPayloadToDeviceRequest
                {
                    User_id = userId,
                    Payload = request.Payload
                };

                var response = await _servicePushNotificationClient.Send_notification_to_deviceAsync(deviceId, serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error sending notification to device: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Broadcast a notification to all users
        /// </summary>
        /// <remarks>
        /// Broadcast a notification to all registered users. This is typically an admin operation.
        /// </remarks>
        /// <param name="request">Broadcast notification request</param>
        /// <returns>Notification response</returns>
        /// <response code="201">Notification broadcast successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("broadcast")]
        // ADR-005 L2: broadcast carried NO auth attribute today (L1 fallback only). Behaviour-preserving
        // = any-authenticated. NOTE: a fleet-wide broadcast is arguably {admin}; the ADR does not
        // enumerate push at L2, so this is annotated participant to avoid silently changing the user type.
        // Flagged to TL/PO for a one-line override to an admin cap if intended.
        [RequireCapability(Capabilities.NotificationPrefsSelf)]
        [ProducesResponseType(typeof(SentPayloadResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SentPayloadResponse>> BroadcastNotification([FromBody] BroadcastNotificationRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new PushNotificationApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var serviceRequest = new SentPayloadToAllUsersRequest
                {
                    Payload = request.Payload
                };

                var response = await _servicePushNotificationClient.Broadcast_notification_to_all_usersAsync(serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error broadcasting notification: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Send a notification to a specific user
        /// </summary>
        /// <remarks>
        /// Send a notification to the authenticated user. The user ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="request">Notification request</param>
        /// <returns>Notification response</returns>
        /// <response code="201">Notification sent successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="404">Not found</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("user")]
        [Authorize]
        [RequireCapability(Capabilities.NotificationPrefsSelf)] // ADR-005 §B send-to-user (any-auth preserved)
        [ProducesResponseType(typeof(SentPayloadResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SentPayloadResponse>> SendNotificationToUser([FromBody] SendNotificationToUserRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new PushNotificationApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var serviceRequest = new SentPayloadToUserRequest
                {
                    Payload = request.Payload
                };

                var response = await _servicePushNotificationClient.Send_notification_to_userAsync(userId, serviceRequest);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error sending notification to user: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Health check endpoint
        /// </summary>
        /// <remarks>
        /// Health check endpoint to verify the push notification service is running
        /// </remarks>
        /// <returns>Health status</returns>
        /// <response code="200">Service is healthy</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("health")]
        [PublicEndpoint("Push-notification health passthrough — ADR-005 §A public.")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<object>> HealthCheck()
        {
            try
            {
                var response = await _servicePushNotificationClient.Health_checkAsync();
                return Ok(response);
            }
            catch (PushNotificationApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new PushNotificationApiException($"Error checking health: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }
    }
}

