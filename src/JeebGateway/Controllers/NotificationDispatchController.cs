using JeebGateway.Auth.Capabilities;
using JeebGateway.Notifications;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// FT-06 reconciliation (JEB-1494 false claim / WS-A JEB-57 dependency).
///
/// Wave-C JEB-1494 claimed that a <c>JeebNotificationDispatcher</c> existed and
/// that its POST endpoint returned 200/202. In reality no such endpoint was
/// registered, so any caller received HTTP 405 Method Not Allowed.
///
/// This controller adds the minimal surface WS-A JEB-57 (weekly batch
/// notifications) depends on:
///
///   POST /v1/notifications/dispatch
///
/// The endpoint accepts a typed dispatch request, hands it to notification-service
/// through <see cref="IGenericEventDispatcher"/>, and returns 202 only once that
/// hand-over is durably owned — 503 otherwise, never a fabricated 202.
///
/// Authorization: service-to-service calls (admin scope or system-internal
/// service token). The endpoint is NOT consumer-facing — it is called by the
/// gateway's own batch jobs and by
/// operator tooling.
/// </summary>
[ApiController]
[Route("v1/notifications")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("notifications")]
[Produces("application/json", "application/problem+json")]
public sealed class NotificationDispatchController : ControllerBase
{
    private readonly IGenericEventDispatcher _events;
    private readonly ILogger<NotificationDispatchController> _log;

    public NotificationDispatchController(
        IGenericEventDispatcher events,
        ILogger<NotificationDispatchController> log)
    {
        _events = events;
        _log = log;
    }

    /// <summary>
    /// POST /v1/notifications/dispatch — hand one notification to notification-service.
    ///
    /// Returns 202 Accepted with the <see cref="DispatchOutcomeDto"/> so the
    /// caller can log per-notification results. Delivery itself is asynchronous and
    /// owner-managed. A failure to obtain durable owner acceptance returns 503.
    /// </summary>
    [HttpPost("dispatch")]
    [RequireCapability(Capabilities.NotificationDispatch)]
    [ProducesResponseType(typeof(DispatchOutcomeDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Dispatch(
        [FromBody] NotificationDispatchRequest? body,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "userId is required.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/notification-dispatch-invalid"
            });
        }

        if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Body))
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "title and body are required.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/notification-dispatch-invalid"
            });
        }

        if (!Enum.TryParse<NotificationTrigger>(body.Trigger ?? string.Empty, ignoreCase: true, out var trigger))
        {
            return BadRequest(new ProblemDetails
            {
                Title  = $"Unrecognised trigger '{body.Trigger}'. Valid values: {string.Join(", ", Enum.GetNames<NotificationTrigger>())}.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/notification-dispatch-invalid"
            });
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = NotificationTriggerRouting.CategoryFor(trigger),
            ["trigger"] = trigger.ToString(),
        };
        foreach (var pair in body.Data ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null) data[pair.Key] = pair.Value;
        }
        if (!string.IsNullOrWhiteSpace(body.Language)) data["language"] = body.Language;

        // The caller's key IS the entity id. Absent, mint a per-call id so an omitted key keeps
        // its documented "no deduplication" meaning instead of collapsing distinct sends.
        var entityId = string.IsNullOrWhiteSpace(body.IdempotencyKey)
            ? $"dispatch:{Guid.NewGuid():N}"
            : body.IdempotencyKey!;

        var classification = await PushHandover.DispatchAsync(
            _events,
            _log,
            JeebGenericEventTypes.NotificationDispatchEventType,
            body.UserId,
            entityId,
            body.Title,
            body.Body,
            data,
            NotificationTriggerRouting.CategoryFor(trigger),
            ct);

        if (!PushHandover.IsProducerOwned(classification))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Notification owner did not durably accept the command.",
                Detail = classification.ToString(),
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/notification-owner-unavailable",
            });
        }

        _log.LogInformation(
            "notification.dispatched userId={UserId} trigger={Trigger} outcome={Outcome}",
            body.UserId, trigger, classification);

        return Accepted(new DispatchOutcomeDto
        {
            UserId    = body.UserId,
            Trigger   = trigger.ToString(),
            Delivered = true,
            Outcome   = classification.ToString(),
            Detail    = null
        });
    }
}

/// <summary>Request body for POST /v1/notifications/dispatch.</summary>
public sealed class NotificationDispatchRequest
{
    /// <summary>Target user id (recipient).</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Notification trigger name. Must match a <see cref="NotificationTrigger"/>
    /// value (case-insensitive). Defaults to <c>Generic</c> when unrecognised.
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>Localised notification title.</summary>
    public string? Title { get; init; }

    /// <summary>Localised notification body.</summary>
    public string? Body { get; init; }

    /// <summary>Optional structured payload forwarded to the mobile client.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>
    /// Optional idempotency key. When provided, the push-notification service
    /// deduplicates on this key so a retry of the batch never double-delivers.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Optional BCP-47 language tag for push localisation.</summary>
    public string? Language { get; init; }
}

/// <summary>202 Accepted response body.</summary>
public sealed class DispatchOutcomeDto
{
    public required string UserId    { get; init; }
    public required string Trigger   { get; init; }
    public required bool   Delivered { get; init; }
    public required string Outcome   { get; init; }
    public string?         Detail    { get; init; }
}
