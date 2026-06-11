using JeebGateway.Auth.Capabilities;
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
/// The endpoint accepts a typed dispatch request, routes it through the
/// gateway's existing <see cref="IPushNotificationService"/> pipeline
/// (preference-filtering, device-token resolution, FCM/APNs transport,
/// retry queue) and returns 202 Accepted with the delivery outcome so the
/// weekly batch can log per-notification results without blocking.
///
/// Authorization: service-to-service calls (admin scope or system-internal
/// service token). The endpoint is NOT consumer-facing — it is called by the
/// gateway's own batch jobs (WeeklySettlementBatch → JEB-57) and by
/// operator tooling.
/// </summary>
[ApiController]
[Route("v1/notifications")]
[Produces("application/json", "application/problem+json")]
public sealed class NotificationDispatchController : ControllerBase
{
    private readonly IPushNotificationService _push;
    private readonly ILogger<NotificationDispatchController> _log;

    public NotificationDispatchController(
        IPushNotificationService push,
        ILogger<NotificationDispatchController> log)
    {
        _push = push;
        _log = log;
    }

    /// <summary>
    /// POST /v1/notifications/dispatch — dispatch a single push notification via
    /// the gateway's unified <see cref="IPushNotificationService"/> pipeline.
    ///
    /// Returns 202 Accepted with the <see cref="DispatchOutcomeDto"/> so the
    /// caller can log per-notification results. The underlying push is
    /// best-effort: a transport failure does NOT 5xx this endpoint — it returns
    /// 202 with <c>delivered=false</c> + the failure reason so batch callers
    /// can collect non-delivery telemetry without aborting the batch.
    /// </summary>
    [HttpPost("dispatch")]
    [RequireCapability(Capabilities.NotificationDispatch)]
    [ProducesResponseType(typeof(DispatchOutcomeDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

        var request = new PushNotificationRequest(
            UserId:         body.UserId,
            Trigger:        trigger,
            Title:          body.Title,
            Body:           body.Body,
            Data:           body.Data,
            IdempotencyKey: body.IdempotencyKey,
            Language:       body.Language);

        PushDeliveryResult result;
        try
        {
            result = await _push.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            // Transport-level exception: surface as 202 with delivered=false
            // so the caller (batch job) can log and continue, not abort.
            _log.LogWarning(ex,
                "notification.dispatch_exception userId={UserId} trigger={Trigger}",
                body.UserId, trigger);

            return Accepted(new DispatchOutcomeDto
            {
                UserId    = body.UserId,
                Trigger   = trigger.ToString(),
                Delivered = false,
                Outcome   = "transport_exception",
                Detail    = ex.GetType().Name
            });
        }

        _log.LogInformation(
            "notification.dispatched userId={UserId} trigger={Trigger} outcome={Outcome}",
            body.UserId, trigger, result.Outcome);

        return Accepted(new DispatchOutcomeDto
        {
            UserId    = body.UserId,
            Trigger   = trigger.ToString(),
            Delivered = result.Outcome is PushDeliveryOutcome.Delivered
                     or PushDeliveryOutcome.DeliveredOnRetry,
            Outcome   = result.Outcome.ToString(),
            Detail    = result.Reason
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
