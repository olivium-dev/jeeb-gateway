using JeebGateway.Push;
using JeebGateway.Requests;
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
    private readonly ILogger<DeliveriesController> _log;

    public DeliveriesController(
        IRequestsStore store,
        IPushNotificationService push,
        ILogger<DeliveriesController> log)
    {
        _store = store;
        _push = push;
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
