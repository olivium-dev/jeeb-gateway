using JeebGateway.Auth.Capabilities;
using JeebGateway.DTOs.Notification;
using JeebGateway.Notifications;
using JeebGateway.Services.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Controllers;

/// <summary>
/// Gateway notification render→dispatch endpoint (JEB-1494).
///
/// <para>Exposes a single <c>POST /api/notifications</c> route that accepts a
/// template key, locale, substitution parameters and a recipient user ID.
/// The request is persisted to an outbox, rendered into a push payload, and
/// dispatched through the existing push-notification pipeline.</para>
///
/// <para>This controller is intentionally separate from the existing
/// <see cref="NotificationController"/> (which proxies notification-service
/// read/status operations) and from <see cref="PushNotificationController"/>
/// (which manages device registration and raw payloads).</para>
/// </summary>
[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
public class JeebNotificationsController : ControllerBase
{
    private readonly IJeebNotificationDispatcher _dispatcher;
    private readonly INotificationOwnerClient _owner;
    private readonly ILogger<JeebNotificationsController> _logger;

    public JeebNotificationsController(
        IJeebNotificationDispatcher dispatcher,
        INotificationOwnerClient owner,
        ILogger<JeebNotificationsController> logger)
    {
        _dispatcher = dispatcher;
        _owner = owner;
        _logger = logger;
    }

    /// <summary>
    /// Dispatch a notification to a user via template render + push.
    /// </summary>
    /// <remarks>
    /// Renders the named template in the requested locale, substitutes the
    /// supplied parameters, then submits it to notification-service for durable
    /// dispatch, retry, tracking, and DLQ ownership.
    ///
    /// Supply <c>Idempotency-Key</c> header to make the call idempotent — duplicate
    /// requests with the same key resolve to the same owner notification ID.
    /// </remarks>
    /// <param name="request">Dispatch request body.</param>
    /// <param name="idempotencyKey">Optional idempotency key from the HTTP header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dispatch result including entry ID and delivery status.</returns>
    /// <response code="202">Notification accepted for dispatch.</response>
    /// <response code="400">Bad request — missing required fields or unknown template key.</response>
    /// <response code="401">Unauthorized.</response>
    /// <response code="403">Forbidden — caller does not hold the <c>notification.dispatch</c> capability.</response>
    /// <response code="503">The notification owner did not durably accept the command.</response>
    [HttpPost]
    [Authorize]
    [RequireCapability(Capabilities.NotificationDispatch)] // ADR-005 §N {admin}
    [ProducesResponseType(typeof(DispatchNotificationResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DispatchNotificationResponseDto>> DispatchNotification(
        [FromBody] DispatchNotificationRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        _logger.LogInformation(
            "Notification dispatch request. TemplateKey={Template} Locale={Locale} Recipient={UserId} IdempotencyKey={Key}",
            request.TemplateKey, request.Locale, request.RecipientUserId, idempotencyKey);

        NotificationDispatchResult result;
        try
        {
            result = await _dispatcher.DispatchAsync(
                request.TemplateKey,
                request.Locale,
                request.Parameters,
                request.RecipientUserId,
                idempotencyKey,
                ct);
        }
        catch (NotificationOwnerConflictException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/notification-idempotency-conflict",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification owner did not durably accept command for {RecipientUserId}",
                request.RecipientUserId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Notification owner is unavailable.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/notification-owner-unavailable",
            });
        }

        if (result.Status == NotificationDispatchStatus.DLQ && !result.WasDeduplicated)
        {
            // JEBV4-63: was an ad-hoc { error } object — now the same RFC7807 envelope
            // every other 4xx on this surface uses.
            return BadRequest(new ProblemDetails
            {
                Title = result.Error ?? "Dispatch failed.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/notification-dispatch-failed"
            });
        }

        var dto = new DispatchNotificationResponseDto
        {
            EntryId = result.EntryId,
            WasDeduplicated = result.WasDeduplicated,
            Status = result.Status.ToString(),
            Error = result.Error
        };

        return Accepted(dto);
    }

    /// <summary>
    /// Returns entries currently in the notification dispatch DLQ (admin observability).
    /// </summary>
    /// <response code="200">DLQ entries returned.</response>
    /// <response code="401">Unauthorized.</response>
    /// <response code="403">Forbidden.</response>
    [HttpGet("dlq")]
    [Authorize]
    [RequireCapability(Capabilities.NotificationDispatch)] // ADR-005 §N {admin}
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDlq(CancellationToken ct = default)
    {
        try
        {
            return Ok(await _owner.GetDeadLettersAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification owner DLQ query failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Notification owner DLQ is unavailable.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/notification-owner-unavailable",
            });
        }
    }
}
