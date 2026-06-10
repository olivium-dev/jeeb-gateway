using JeebGateway.Auth.Capabilities;
using JeebGateway.DTOs.Notification;
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
    private readonly INotificationDispatchOutbox _outbox;
    private readonly ILogger<JeebNotificationsController> _logger;

    public JeebNotificationsController(
        IJeebNotificationDispatcher dispatcher,
        INotificationDispatchOutbox outbox,
        ILogger<JeebNotificationsController> logger)
    {
        _dispatcher = dispatcher;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>
    /// Dispatch a notification to a user via template render + push.
    /// </summary>
    /// <remarks>
    /// Renders the named template in the requested locale, substitutes the
    /// supplied parameters, then dispatches the result through the existing
    /// push-notification pipeline with outbox durability and retry.
    ///
    /// Supply <c>Idempotency-Key</c> header to make the call idempotent — duplicate
    /// requests with the same key are silently deduplicated.
    /// </remarks>
    /// <param name="request">Dispatch request body.</param>
    /// <param name="idempotencyKey">Optional idempotency key from the HTTP header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dispatch result including entry ID and delivery status.</returns>
    /// <response code="202">Notification accepted for dispatch.</response>
    /// <response code="400">Bad request — missing required fields or unknown template key.</response>
    /// <response code="401">Unauthorized.</response>
    /// <response code="403">Forbidden — caller does not hold the <c>notification.dispatch</c> capability.</response>
    /// <response code="500">Internal server error.</response>
    [HttpPost]
    [Authorize]
    [RequireCapability(Capabilities.NotificationDispatch)] // ADR-005 §N {admin}
    [ProducesResponseType(typeof(DispatchNotificationResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
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

        var result = await _dispatcher.DispatchAsync(
            request.TemplateKey,
            request.Locale,
            request.Parameters,
            request.RecipientUserId,
            idempotencyKey,
            ct);

        if (result.Status == NotificationDispatchStatus.DLQ && !result.WasDeduplicated)
        {
            return BadRequest(new { error = result.Error ?? "Dispatch failed." });
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
    [ProducesResponseType(typeof(IReadOnlyList<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<object>>> GetDlq(CancellationToken ct = default)
    {
        var entries = await _outbox.GetDlqAsync(ct);
        return Ok(entries.Select(e => new
        {
            e.Id,
            e.TemplateKey,
            e.Locale,
            e.RecipientUserId,
            e.AttemptCount,
            e.LastError,
            e.CreatedAt,
            e.IdempotencyKey
        }).ToList());
    }
}
