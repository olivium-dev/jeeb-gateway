using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Read surface for the caller's notification feed. Unlike
/// <see cref="NotificationPreferencesController"/> (which holds gateway-local
/// toggle state), this controller proxies the REAL notification-service
/// (FastAPI, Mongo <c>jeeb_notifications</c>) via
/// <see cref="INotificationServiceClient"/> — the list call resolves to
/// <c>GET /notifications?receiver={userId}</c> upstream, which queries the
/// notification DB.
///
/// Gated by <c>FeatureFlags:UseUpstream:Notification</c>: when the flag is off
/// (default, so existing fixtures stay green) the endpoint returns an empty
/// page rather than calling an unconfigured downstream. When on, every request
/// reaches the notification-service DB through the named "notification"
/// HttpClient + standard resilience pipeline.
/// </summary>
[ApiController]
[Route("users/me/notifications")]
public class NotificationsController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly INotificationServiceClient _notifications;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public NotificationsController(
        INotificationServiceClient notifications,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _notifications = notifications;
        _flags = flags;
    }

    /// <summary>
    /// Lists the authenticated user's notifications. Real path: proxies
    /// <c>GET /notifications</c> on notification-service, filtered by the
    /// caller's id as <c>receiver</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        if (!_flags.CurrentValue.Notification)
        {
            // Flag off: do not call an unconfigured downstream. Return an empty
            // page so callers get a well-formed envelope during phased rollout.
            // KILL SWITCH — retained intentionally: flipping
            // FeatureFlags:UseUpstream:Notification back to false instantly
            // reverts the feed to this empty-page response without a redeploy.
            // Do NOT delete this branch.
            return Ok(new NotificationListResponse
            {
                Page = page,
                PageSize = pageSize,
                TotalNotifications = 0,
                TotalPages = 0,
                HasNext = false,
                HasPrevious = false,
                Notifications = Array.Empty<NotificationListItem>(),
            });
        }

        var result = await _notifications.GetByReceiverAsync(userId, page, pageSize, ct);
        return Ok(result);
    }
}
