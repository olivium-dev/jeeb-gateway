using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Controllers;

/// <summary>
/// Delivery status state-machine endpoint (T-backend-013, JEEB-31).
///
/// PATCH /deliveries/{id}/status drives a single linear lifecycle:
///   pending → matched → accepted → picked_up → heading_off → delivered → rated
///
/// Side-effects committed per transition:
/// <list type="bullet">
///   <item>Each transition pushes a <see cref="NotificationTrigger.StatusChange"/>
///     to the "other party" (Client → Jeeber once a Jeeber is bound to the
///     row, and Jeeber → Client). Pre-accept transitions notify the Client
///     only because no Jeeber is bound yet.</item>
///   <item><c>picked_up</c> flips <see cref="DeliveryRequest.GpsTrackingActive"/>
///     true so downstream telemetry can start ingesting Jeeber location
///     updates.</item>
///   <item><c>heading_off → delivered</c> requires the OTP previously
///     issued to the Client at accept-time; a missing or mismatched value
///     rejects with 400.</item>
/// </list>
///
/// Anything else — skipping a step, going backwards, leaving a terminal
/// state, supplying an unknown status string — is rejected with 400 by
/// the <see cref="DeliveryStateMachine"/>.
/// </summary>
[ApiController]
[Route("deliveries")]
public class DeliveriesController : ControllerBase
{
    private readonly IRequestsStore _store;
    private readonly IPushNotificationService _push;
    private readonly ICancellationService _cancellations;
    private readonly ILogger<DeliveriesController> _log;

    public DeliveriesController(
        IRequestsStore store,
        IPushNotificationService push,
        ICancellationService cancellations,
        ILogger<DeliveriesController> log)
    {
        _store = store;
        _push = push;
        _cancellations = cancellations;
        _log = log;
    }

    [HttpPatch("{deliveryId}/status")]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchStatus(
        string deliveryId,
        [FromBody] PatchStatusBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Status))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "status is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-transition"
            });
        }

        var result = await _store.TryTransitionAsync(deliveryId, body.Status, body.Otp, ct);

        switch (result.Outcome)
        {
            case DeliveryTransitionOutcome.NotFound:
                return NotFound();

            case DeliveryTransitionOutcome.InvalidTransition:
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid status transition.",
                    Detail = result.Reason,
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/invalid-transition"
                });

            case DeliveryTransitionOutcome.OtpRequired:
                return BadRequest(new ProblemDetails
                {
                    Title = "OTP is required to mark the delivery as delivered.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-required"
                });

            case DeliveryTransitionOutcome.OtpMismatch:
                return BadRequest(new ProblemDetails
                {
                    Title = "Supplied OTP does not match.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-mismatch"
                });

            case DeliveryTransitionOutcome.Committed:
                // Status flipped — notify the counterparty. Pushes are fire-
                // and-forget per T-backend-022: a transient failure must
                // not roll back the state transition, so we don't await
                // failures here, just log them.
                await NotifyOtherPartyAsync(result.Request!, result.PreviousStatus!, ct);
                return Ok(ToDto(result.Request!));

            default:
                // Defensive — every outcome is handled above. If a new
                // enum value lands without a controller branch, fail
                // closed rather than returning a misleading 200.
                return Problem(
                    title: "Unhandled transition outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// T-backend-024 (JEEB-42): cancellation endpoint.
    ///
    /// Routing depends on caller role and current status:
    /// <list type="bullet">
    ///   <item>Client, status before <c>picked_up</c> → row goes terminal
    ///     to <c>cancelled</c> immediately. No penalty.</item>
    ///   <item>Client, status <c>picked_up</c> or <c>heading_off</c> →
    ///     row parks on <c>cancellation_requested</c>; the admin queue
    ///     is the only path forward (approve / reject).</item>
    ///   <item>Jeeber, status <c>accepted</c> onwards → reason field
    ///     mandatory; on commit the service consults the rolling-7d
    ///     cancellation count for that Jeeber and applies a 24-hour
    ///     no-new-offers restriction when the count hits 3+.</item>
    /// </list>
    ///
    /// Counterparty push fires on every committed cancel so the other
    /// party finds out without polling.
    /// </summary>
    [HttpPost("{deliveryId}/cancel")]
    [ProducesResponseType(typeof(CancelDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string deliveryId,
        [FromBody] CancelDeliveryBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var callerIsClient = UserIdentity.HasRole(HttpContext, Roles.Client);
        var callerIsJeeber = UserIdentity.HasRole(HttpContext, Roles.Jeeber);

        if (!callerIsClient && !callerIsJeeber)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Cancel requires the customer or driver role.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-role"
            });
        }

        var result = await _cancellations.CancelAsync(
            deliveryId, callerId, callerIsClient, callerIsJeeber, body?.Reason, ct);

        switch (result.Outcome)
        {
            case CancellationOutcome.NotFound:
                return NotFound();

            case CancellationOutcome.NotAuthorized:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "You are not a party to this delivery.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/not-a-party"
                });

            case CancellationOutcome.ReasonRequired:
                return BadRequest(new ProblemDetails
                {
                    Title = "Reason is required when a driver cancels a delivery.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/cancellation-reason-required"
                });

            case CancellationOutcome.NotCancellable:
                return Conflict(new ProblemDetails
                {
                    Title = "Delivery cannot be cancelled in its current state.",
                    Detail = $"current status: {result.Request?.Status}",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/not-cancellable"
                });

            case CancellationOutcome.CancelledImmediately:
            case CancellationOutcome.CancelledByJeeber:
            case CancellationOutcome.PendingAdminApproval:
                await NotifyCancellationCounterpartyAsync(result.Request!, result.PreviousStatus!, result.Outcome, ct);
                return Ok(new CancelDeliveryResponse
                {
                    DeliveryId = result.Request!.Id,
                    Status = result.Request.Status,
                    PreviousStatus = result.PreviousStatus!,
                    Reason = result.Reason,
                    PendingApproval = result.Outcome == CancellationOutcome.PendingAdminApproval,
                    JeeberRestricted = result.JeeberRestrictionTriggered,
                    RestrictionExpiresAt = result.RestrictionExpiresAt,
                    JeeberCancellationsLast7Days = result.JeeberCancellationsLast7Days
                });

            default:
                return Problem(
                    title: "Unhandled cancellation outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task NotifyCancellationCounterpartyAsync(
        DeliveryRequest req,
        string previousStatus,
        CancellationOutcome outcome,
        CancellationToken ct)
    {
        var recipients = new List<string> { req.ClientId };
        if (!string.IsNullOrEmpty(req.JeeberId))
        {
            recipients.Add(req.JeeberId);
        }

        var data = new Dictionary<string, string>
        {
            ["deliveryId"] = req.Id,
            ["previousStatus"] = previousStatus,
            ["status"] = req.Status,
            ["cancelledBy"] = req.CancelledBy ?? string.Empty,
            ["pendingApproval"] = (outcome == CancellationOutcome.PendingAdminApproval) ? "true" : "false"
        };

        var title = outcome == CancellationOutcome.PendingAdminApproval
            ? "Cancellation requested"
            : "Delivery cancelled";
        var bodyText = outcome == CancellationOutcome.PendingAdminApproval
            ? "The client requested a cancellation. An admin will review."
            : $"Delivery cancelled from {previousStatus}.";

        foreach (var userId in recipients)
        {
            try
            {
                var request = new PushNotificationRequest(
                    UserId: userId,
                    Trigger: NotificationTrigger.StatusChange,
                    Title: title,
                    Body: bodyText,
                    Data: data,
                    IdempotencyKey: $"{req.Id}:{req.Status}:cancel:{userId}");
                await _push.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Cancellation push failed for delivery {DeliveryId} user {UserId}",
                    req.Id, userId);
            }
        }
    }

    /// <summary>
    /// Pushes a <see cref="NotificationTrigger.StatusChange"/> to the user
    /// on the opposite side of the delivery. Pre-accept transitions
    /// (pending → matched) have no Jeeber bound yet — those notify the
    /// Client only. Post-accept transitions notify both directions of the
    /// pair when applicable; the canonical "other party" for a Jeeber
    /// action is the Client and vice versa.
    /// </summary>
    private async Task NotifyOtherPartyAsync(DeliveryRequest req, string previousStatus, CancellationToken ct)
    {
        // The counterparty depends on the transition:
        //   * pending → matched: notify Client (no Jeeber yet).
        //   * everything else:   notify Client and Jeeber both, since the
        //     PATCH could come from either side and the spec says
        //     "notification to the other party". We send to both so the
        //     gateway doesn't need to know who initiated the patch.
        var recipients = new List<string> { req.ClientId };
        if (!string.IsNullOrEmpty(req.JeeberId))
        {
            recipients.Add(req.JeeberId);
        }

        var data = new Dictionary<string, string>
        {
            ["deliveryId"] = req.Id,
            ["previousStatus"] = previousStatus,
            ["status"] = req.Status,
            ["gpsTrackingActive"] = req.GpsTrackingActive ? "true" : "false"
        };

        var title = "Delivery status updated";
        var bodyText = $"Status changed from {previousStatus} to {req.Status}.";

        foreach (var userId in recipients)
        {
            try
            {
                var request = new PushNotificationRequest(
                    UserId: userId,
                    Trigger: NotificationTrigger.StatusChange,
                    Title: title,
                    Body: bodyText,
                    Data: data,
                    IdempotencyKey: $"{req.Id}:{req.Status}:{userId}");

                await _push.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                // Push delivery is best-effort; the state transition has
                // already committed. Log so observability picks it up but
                // do not bubble the failure back to the caller.
                _log.LogWarning(ex,
                    "Status-change push failed for delivery {DeliveryId} user {UserId}",
                    req.Id, userId);
            }
        }
    }

    private static DeliveryRequestDto ToDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt,
        GpsTrackingActive = r.GpsTrackingActive
    };
}
