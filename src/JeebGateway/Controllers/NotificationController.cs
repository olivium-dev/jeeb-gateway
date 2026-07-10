using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.DTOs.Notification;
using JeebGateway.Notifications;
using JeebGateway.Users;
using JeebGateway.service.ServiceNotification;
using Newtonsoft.Json.Linq;
using NotificationApiException = JeebGateway.service.ServiceNotification.ApiException;

namespace JeebGateway.Controllers
{
    /// <summary>
    /// Controller for managing user notifications (listing and read/unread status)
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class NotificationController : ControllerBase
    {
        private readonly ServiceNotificationClient _serviceNotificationClient;
        private readonly ILogger<NotificationController> _logger;

        public NotificationController(
            ServiceNotificationClient serviceNotificationClient,
            ILogger<NotificationController> logger)
        {
            _serviceNotificationClient = serviceNotificationClient;
            _logger = logger;
        }

        private ActionResult<(string userId, bool isValid)> ValidateUserAndServices()
        {
            // NOT-02 fix: resolve identity via the gateway-canonical UserIdentity helper so the
            // inbox is reachable on BOTH the bearer path (sid/sub claims) AND the trusted edge
            // X-User-Id header path. The previous claim-only lookup made the inbox return 401 for
            // every edge-injected caller — the exact path the mobile client takes through the edge.
            if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out _))
            {
                throw new NotificationApiException("Unauthorized: User ID not found in token or X-User-Id header", 401, "Unauthorized", new Dictionary<string, IEnumerable<string>>(), null);
            }

            if (_serviceNotificationClient == null)
            {
                throw new NotificationApiException("Error: ServiceNotificationClient is not initialized", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }

            return (userId, true);
        }

        /// <summary>
        /// JEBV4-250 — map a caught upstream <see cref="NotificationApiException"/> to a
        /// sanitized RFC 7807 result: the upstream status is preserved (clamped to a
        /// valid 4xx/5xx; anything else becomes 502 Bad Gateway), but the upstream
        /// exception message / response body is logged server-side ONLY, never echoed
        /// to the caller. Mirrors <c>ChatController.UpstreamProblem</c> (JEBV4-242).
        /// </summary>
        private ActionResult UpstreamProblem(NotificationApiException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            _logger.LogWarning(ex,
                "Notification BFF: notification-service call failed on {Method} {Path} → {Status}.",
                Request.Method, Request.Path, status);

            return Problem(
                title: "The notification request could not be completed.",
                statusCode: status);
        }

        /// <summary>
        /// List notifications messages for the authenticated user
        /// </summary>
        /// <remarks>
        /// Retrieves a paginated list of messages for the authenticated user with optional read status filtering.
        /// </remarks>
        /// <param name="page">Page number (starts from 1)</param>
        /// <param name="pageSize">Number of items per page (max 100)</param>
        /// <param name="readStatus">
        /// Filter by read status: 'read' (status=read), 'unread' (status=delivered/not delivered/unread), 'all' (no filter).
        /// </param>
        /// <returns>Messages retrieved successfully</returns>
        /// <response code="200">Messages retrieved successfully</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="422">Validation error</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("messages")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B self / any-auth
        [ProducesResponseType(typeof(PagedNotificationMessagesResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<PagedNotificationMessagesResponseDto>> GetMessagesForCurrentUser(
            [FromQuery] int? page = null,
            [FromQuery] int? pageSize = null,
            [FromQuery] string? readStatus = null)
        {
            try
            {
                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var response = await _serviceNotificationClient
                    .Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
                        userId,
                        page,
                        pageSize,
                        readStatus,
                        notification_type: null,
                        sender: null,
                        created_after: null,
                        created_before: null);

                var dto = MapToPagedNotificationMessagesResponseDto(response, page, pageSize);
                return Ok(dto);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error retrieving messages for current user");
                throw new NotificationApiException(
                    $"Error retrieving messages: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Unread notification count for the authenticated user (bell-badge surface — NOT-02).
        /// </summary>
        /// <remarks>
        /// Returns the number of unread notifications for the current user. The count is capped
        /// at a display ceiling so the badge never has to render an unbounded integer; when the
        /// true count exceeds the ceiling, <c>capped=true</c> and the client renders e.g. "99+".
        /// </remarks>
        /// <response code="200">Unread count retrieved successfully</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("messages/unread-count")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B self / any-auth
        [ProducesResponseType(typeof(UnreadNotificationCountResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<UnreadNotificationCountResponseDto>> GetUnreadCountForCurrentUser()
        {
            // Display ceiling for the badge. The notification-service receiver list exposes a
            // `total` for the filtered (unread) query, so we read page 1 with a ceiling-sized
            // page and derive the count + capped flag without iterating every page.
            const int BadgeCeiling = 99;

            try
            {
                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var userId = validationResult.Value.userId;

                var response = await _serviceNotificationClient
                    .Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
                        userId,
                        page: 1,
                        page_size: BadgeCeiling + 1,
                        read_status: "unread",
                        notification_type: null,
                        sender: null,
                        created_after: null,
                        created_before: null);

                var (count, capped) = ResolveUnreadCount(response, BadgeCeiling);

                return Ok(new UnreadNotificationCountResponseDto
                {
                    UnreadCount = count,
                    Capped = capped
                });
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error retrieving unread count for current user");
                throw new NotificationApiException(
                    $"Error retrieving unread count: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Mark a notification as read
        /// </summary>
        /// <param name="notificationId">Notification ID</param>
        /// <returns>Update result</returns>
        /// <response code="200">Notification marked as read</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Notification not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPatch("notifications/{notificationId}/read")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B (STATE: ownership in-action)
        [ProducesResponseType(typeof(NotificationStatusResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<NotificationStatusResponseDto>> MarkNotificationRead([FromRoute] string notificationId)
        {
            try
            {
                if (string.IsNullOrEmpty(notificationId))
                {
                    throw new NotificationApiException("NotificationId is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceNotificationClient
                    .Mark_notification_read_notifications__notification_id__mark_read_patchAsync(notificationId);

                var dto = new NotificationStatusResponseDto
                {
                    Success = true,
                    Message = "Notification marked as read."
                };

                return Ok(dto);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error marking notification as read");
                throw new NotificationApiException(
                    $"Error marking notification as read: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Mark a notification as unread
        /// </summary>
        /// <param name="notificationId">Notification ID</param>
        /// <returns>Update result</returns>
        /// <response code="200">Notification marked as unread</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Notification not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPatch("notifications/{notificationId}/unread")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B (STATE: ownership in-action)
        [ProducesResponseType(typeof(NotificationStatusResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<NotificationStatusResponseDto>> MarkNotificationUnread([FromRoute] string notificationId)
        {
            try
            {
                if (string.IsNullOrEmpty(notificationId))
                {
                    throw new NotificationApiException("NotificationId is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceNotificationClient
                    .Mark_notification_unread_notifications__notification_id__mark_unread_patchAsync(notificationId);

                var dto = new NotificationStatusResponseDto
                {
                    Success = true,
                    Message = "Notification marked as unread."
                };

                return Ok(dto);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error marking notification as unread");
                throw new NotificationApiException(
                    $"Error marking notification as unread: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Bulk mark notifications as read
        /// </summary>
        /// <param name="request">Bulk mark request containing notification IDs</param>
        /// <returns>Bulk update result</returns>
        /// <response code="200">Notifications marked as read successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPatch("notifications/bulk/read")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B (STATE: ownership in-action)
        [ProducesResponseType(typeof(NotificationStatusResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<NotificationStatusResponseDto>> BulkMarkNotificationsRead([FromBody] BulkMarkNotificationsRequestDto request)
        {
            try
            {
                if (request == null || request.NotificationIds == null || request.NotificationIds.Count == 0)
                {
                    throw new NotificationApiException("Notification_ids list cannot be null or empty", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var serviceRequest = new BulkMarkRequest
                {
                    Notification_ids = request.NotificationIds
                };

                await _serviceNotificationClient
                    .Bulk_mark_notifications_read_notifications_bulk_mark_read_patchAsync(serviceRequest);

                var dto = new NotificationStatusResponseDto
                {
                    Success = true,
                    Message = "Notifications marked as read."
                };

                return Ok(dto);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error bulk marking notifications as read");
                throw new NotificationApiException(
                    $"Error bulk marking notifications as read: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Bulk mark notifications as unread
        /// </summary>
        /// <param name="request">Bulk mark request containing notification IDs</param>
        /// <returns>Bulk update result</returns>
        /// <response code="200">Notifications marked as unread successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPatch("notifications/bulk/unread")]
        [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B (STATE: ownership in-action)
        [ProducesResponseType(typeof(NotificationStatusResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<NotificationStatusResponseDto>> BulkMarkNotificationsUnread([FromBody] BulkMarkNotificationsRequestDto request)
        {
            try
            {
                if (request == null || request.NotificationIds == null || request.NotificationIds.Count == 0)
                {
                    throw new NotificationApiException("Notification_ids list cannot be null or empty", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var serviceRequest = new BulkMarkRequest
                {
                    Notification_ids = request.NotificationIds
                };

                await _serviceNotificationClient
                    .Bulk_mark_notifications_unread_notifications_bulk_mark_unread_patchAsync(serviceRequest);

                var dto = new NotificationStatusResponseDto
                {
                    Success = true,
                    Message = "Notifications marked as unread."
                };

                return Ok(dto);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error bulk marking notifications as unread");
                throw new NotificationApiException(
                    $"Error bulk marking notifications as unread: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        /// <summary>
        /// Health check endpoint for the notification service
        /// </summary>
        /// <returns>Health status</returns>
        /// <response code="200">Service is healthy</response>
        /// <response code="503">Service is unhealthy</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("health")]
        [PublicEndpoint("Notification-service health passthrough — ADR-005 §A public.")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<object>> Health()
        {
            try
            {
                var response = await _serviceNotificationClient.Health_check_health_getAsync();
                return Ok(response);
            }
            catch (NotificationApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during notification service health check");
                throw new NotificationApiException(
                    $"Error checking notification service health: {ex.Message}, Stack trace: {ex.StackTrace}",
                    500,
                    "Internal Server Error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null);
            }
        }

        #region Mapping Methods

        private PagedNotificationMessagesResponseDto MapToPagedNotificationMessagesResponseDto(
            object serviceResponse,
            int? page,
            int? pageSize)
        {
            // The notification service returns a dynamic object; we project only commonly-used fields.
            var dto = new PagedNotificationMessagesResponseDto
            {
                Page = page ?? 1,
                PageSize = pageSize ?? 0,
                TotalCount = 0
            };

            if (serviceResponse is not null)
            {
                // Try to interpret the response as a dynamic object with "items" and "total" fields.
                // If the shape is different, we still return an empty items list with paging info.
                try
                {
                    dynamic dyn = serviceResponse;

                    // Total count (if available)
                    try
                    {
                        dto.TotalCount = (int)(dyn.total ?? dyn.Total ?? 0);
                    }
                    catch
                    {
                        dto.TotalCount = 0;
                    }

                    // Items (if available)
                    if (dyn.items != null)
                    {
                        foreach (var item in dyn.items)
                        {
                            var notificationDto = new NotificationMessageDto();

                            try { notificationDto.NotificationId = (string)(item.notification_id ?? item.Notification_id ?? string.Empty); } catch { }
                            try { notificationDto.Title = (string)(item.title ?? item.Title ?? string.Empty); } catch { }
                            try { notificationDto.Subtitle = (string)(item.subtitle ?? item.Subtitle ?? string.Empty); } catch { }
                            try { notificationDto.Description = (string)(item.description ?? item.Description ?? string.Empty); } catch { }
                            try { notificationDto.NotificationType = (string)(item.notification_type ?? item.Notification_type ?? string.Empty); } catch { }
                            try { notificationDto.Deactivated = (bool)(item.deactivated ?? item.Deactivated ?? false); } catch { }

                            // NOT-02 — primary entity id used to build the deep-link, if the
                            // upstream payload carries one under any of the common field names.
                            try
                            {
                                notificationDto.EntityId = (string)(
                                    item.entity_id ?? item.Entity_id ??
                                    item.reference_id ?? item.Reference_id ??
                                    item.target_id ?? item.Target_id ?? string.Empty);
                            }
                            catch { }

                            try
                            {
                                // Treat status == "read" (case-insensitive) as read
                                var status = (string)(item.status ?? item.Status ?? string.Empty);
                                notificationDto.IsRead = string.Equals(status, "read", StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                notificationDto.IsRead = false;
                            }

                            try
                            {
                                notificationDto.CreatedAt = (DateTimeOffset)(item.created_at ?? item.Created_at);
                            }
                            catch
                            {
                                notificationDto.CreatedAt = DateTimeOffset.MinValue;
                            }

                            // NOT-02 — resolve the client deep-link from the (opaque) type +
                            // optional entity id. Pure, total mapping; never throws.
                            notificationDto.DeepLink = NotificationDeepLinkResolver.Resolve(
                                notificationDto.NotificationType,
                                notificationDto.EntityId);

                            dto.Items.Add(notificationDto);
                        }
                    }
                }
                catch
                {
                    // Swallow mapping issues and return a minimal DTO; logging already happens at controller level.
                }
            }

            return dto;
        }

        /// <summary>
        /// Derive the unread badge count from the (unread-filtered) receiver response.
        /// Prefers the upstream <c>total</c>; falls back to counting returned <c>items</c>.
        /// Caps at <paramref name="ceiling"/> and reports whether the count was capped.
        /// </summary>
        private static (int count, bool capped) ResolveUnreadCount(object? serviceResponse, int ceiling)
        {
            if (serviceResponse is null)
            {
                return (0, false);
            }

            int total = 0;
            try
            {
                // Normalize via JObject so the count math works for both the real
                // NSwag client (which returns a Newtonsoft JObject from its HTTP path)
                // AND any stub/test double that returns a C# anonymous type (which
                // cannot be accessed via dynamic across assembly boundaries).
                JObject jo;
                if (serviceResponse is JObject directJo)
                {
                    jo = directJo;
                }
                else
                {
                    jo = JObject.FromObject(serviceResponse);
                }

                bool gotTotal = false;
                var totalToken = jo["total"] ?? jo["Total"];
                if (totalToken != null)
                {
                    total = totalToken.Value<int>();
                    gotTotal = true;
                }

                if (!gotTotal)
                {
                    // No total field — count the items we were given (page sized to ceiling+1).
                    var itemsToken = jo["items"] ?? jo["Items"];
                    if (itemsToken is JArray arr)
                    {
                        total = arr.Count;
                    }
                }
            }
            catch
            {
                total = 0;
            }

            if (total < 0) total = 0;

            return total > ceiling ? (ceiling, true) : (total, false);
        }

        #endregion
    }
}


