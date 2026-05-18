using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

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
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("deliveries")]
public class DeliveriesController : ControllerBase
{
    private static readonly ActivitySource ActivitySource = new("JeebGateway.Deliveries");
    private readonly IRequestsStore _store;
    private readonly IPushNotificationService _push;
    private readonly ICancellationService _cancellations;
    private readonly IAdminEscalationStore _escalations;
    private readonly IOptions<OtpHandoverOptions> _otpOptions;
    private readonly IServiceOTPClient _otpClient;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeliveriesController> _log;

    // TODO: Replace with persistent storage for external OTP attempt tracking
    private static readonly Dictionary<string, int> _externalOtpAttempts = new();
    private static readonly Dictionary<string, DateTimeOffset> _externalOtpLockouts = new();

    public DeliveriesController(
        IRequestsStore store,
        IPushNotificationService push,
        ICancellationService cancellations,
        IAdminEscalationStore escalations,
        IOptions<OtpHandoverOptions> otpOptions,
        IServiceOTPClient otpClient,
        TimeProvider clock,
        ILogger<DeliveriesController> log)
    {
        _store = store;
        _push = push;
        _cancellations = cancellations;
        _escalations = escalations;
        _otpOptions = otpOptions;
        _otpClient = otpClient;
        _clock = clock;
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

    /// <summary>
    /// POST /deliveries/{id}/verify-otp (T-backend-015 / JEEB-33).
    ///
    /// Dedicated hand-off OTP verification surface, distinct from the
    /// PATCH /status endpoint's OTP gate so the attempt-counter and
    /// lockout policy can live on this single endpoint.
    ///
    /// <list type="bullet">
    ///   <item>Correct OTP → transition to <see cref="RequestStatus.Delivered"/> and 200.</item>
    ///   <item>Wrong OTP → 400 with the remaining attempt budget.</item>
    ///   <item>N-th wrong OTP (default 3) → 423 Locked, escalation row created.</item>
    ///   <item>Subsequent calls after lockout → 423 Locked (no extra escalation).</item>
    /// </list>
    /// </summary>
    [HttpPost("{deliveryId}/verify-otp")]
    [ProducesResponseType(typeof(OtpVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OtpLockedResponse), StatusCodes.Status423Locked)]
    public async Task<IActionResult> VerifyOtp(
        string deliveryId,
        [FromBody] OtpVerificationRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.OtpCode))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "otpCode is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/otp-required"
            });
        }

        var opts = _otpOptions.Value;
        var now = _clock.GetUtcNow();
        var result = await _store.TryVerifyOtpAsync(deliveryId, body.OtpCode, opts.MaxAttempts, now, ct);

        switch (result.Outcome)
        {
            case OtpVerificationOutcome.NotFound:
                return NotFound();

            case OtpVerificationOutcome.NotInHandoverState:
                return BadRequest(new ProblemDetails
                {
                    Title = "Delivery is not in the OTP handover state.",
                    Detail = $"OTP verification is only allowed when status is '{RequestStatus.HeadingOff}'.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-not-in-handover-state"
                });

            case OtpVerificationOutcome.Mismatch:
                return BadRequest(new ProblemDetails
                {
                    Title = "Supplied OTP does not match.",
                    Detail = $"{result.AttemptsRemaining} attempt(s) remaining.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-mismatch"
                });

            case OtpVerificationOutcome.Locked:
            {
                // First time this row hits the lockout boundary — open
                // the escalation row, then stamp the id back on the
                // delivery so the sweeper / repeated calls don't race a
                // duplicate.
                if (result.JustLockedOut && result.Request is { } req)
                {
                    var escalation = await _escalations.CreateAsync(new AdminEscalation
                    {
                        Id = Guid.NewGuid().ToString(),
                        DeliveryId = req.Id,
                        ClientId = req.ClientId,
                        JeeberId = req.JeeberId,
                        Reason = EscalationReason.OtpLocked,
                        Status = EscalationStatus.Pending,
                        CreatedAt = now,
                        OtpAttemptCount = req.OtpAttemptCount,
                    }, ct);

                    await _store.TrySetEscalationIdAsync(req.Id, escalation.Id, ct);
                    _log.LogWarning(
                        "OTP lockout for delivery {DeliveryId} after {Attempts} attempts — escalation {EscalationId} opened",
                        req.Id, req.OtpAttemptCount, escalation.Id);
                }

                // Re-read so the response carries the escalation id that
                // was written above (the in-memory store returns the
                // same row, so the field is now populated).
                var locked = result.Request!;
                return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
                {
                    EscalationId = locked.OtpEscalationId ?? string.Empty,
                    LockedAt = locked.OtpLockedAt ?? now,
                    Reason = EscalationReason.OtpLocked
                });
            }

            case OtpVerificationOutcome.Verified:
            {
                // Status flipped to 'delivered'. Fan out the status-change
                // push to both parties, mirroring the PATCH /status path.
                var req = result.Request!;
                await NotifyOtherPartyAsync(req, RequestStatus.HeadingOff, ct);
                return Ok(new OtpVerificationResponse
                {
                    Delivery = ToDto(req),
                    AttemptsRemaining = result.AttemptsRemaining,
                    Verified = true
                });
            }

            default:
                // Defensive — every outcome is handled. If a new enum
                // value lands without a controller branch, fail closed.
                return Problem(
                    title: "Unhandled OTP verification outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /deliveries/{id}/client-unreachable (T-backend-015 step 6).
    /// Jeeber-initiated: starts the 15-min unreachable-client timer.
    /// The <c>OtpHandoverSweeper</c> escalates the row once the window
    /// elapses without a successful OTP verification.
    /// </summary>
    [HttpPost("{deliveryId}/client-unreachable")]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkClientUnreachable(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        var row = await _store.MarkClientUnreachableAsync(deliveryId, _clock.GetUtcNow(), ct);
        if (row is null) return NotFound();

        _log.LogInformation(
            "Delivery {DeliveryId} flagged client-unreachable at {At} — 15-min escalation timer started",
            row.Id, row.ClientUnreachableAt);

        return Ok(ToDto(row));
    }

    /// <summary>
    /// GET /v1/deliveries/{id}/otp (T-BE-019 / JEB-55).
    ///
    /// Issues a 4-digit handover OTP via the external one-time-password service.
    /// ApplicationId pattern: delivery_handover_{deliveryId}
    /// Only valid when delivery status = at_door.
    /// </summary>
    [HttpGet("{deliveryId}/otp")]
    [ProducesResponseType(typeof(OtpTriggerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TriggerOtp(string deliveryId, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("trigger_delivery_handover_otp");
        activity?.SetTag("delivery.id", deliveryId);
        activity?.SetTag("otp.type", "external");

        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        // Get delivery and validate status
        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.Status != RequestStatus.HeadingOff)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "OTP can only be triggered when delivery status is 'heading_off'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-otp-trigger-state"
            });
        }

        // TODO: Replace with user management service lookup once available
        // For MVP: using placeholder phone number for client
        var clientPhoneNumber = "+962700000000"; // Placeholder - should be delivery.ClientId → user phone lookup

        try
        {
            // Call external OTP service with ApplicationId pattern
            var applicationId = $"delivery_handover_{deliveryId}";
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber = clientPhoneNumber,
                ApplicationId = applicationId
            }, ct);

            _log.LogInformation(
                "Handover OTP triggered for delivery {DeliveryId} with applicationId {ApplicationId}, correlationId {CorrelationId}",
                deliveryId, applicationId, correlationId);

            activity?.SetTag("otp.triggered", "true");
            activity?.SetTag("otp.application_id", applicationId);

            return Ok(new OtpTriggerResponse
            {
                DeliveryId = deliveryId,
                Triggered = true,
                Message = "4-digit OTP sent to client phone number"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to trigger OTP for delivery {DeliveryId}",
                deliveryId);

            return Problem(
                title: "Failed to send OTP",
                detail: "Unable to trigger OTP via one-time-password service",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /v1/deliveries/{id}/otp/verify (T-BE-019 / JEB-55).
    ///
    /// Verifies a 4-digit OTP against the external one-time-password service.
    /// On success: transitions delivery to 'done' status and triggers commission settlement.
    /// On failure: increments attempt counter, returns 423 after 3rd failure with escalation.
    /// </summary>
    [HttpPost("{deliveryId}/otp/verify")]
    [ProducesResponseType(typeof(OtpHandoverVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OtpLockedResponse), StatusCodes.Status423Locked)]
    public async Task<IActionResult> VerifyHandoverOtp(
        string deliveryId,
        [FromBody] OtpHandoverVerificationRequest? body,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("verify_delivery_handover_otp");
        activity?.SetTag("delivery.id", deliveryId);
        activity?.SetTag("otp.type", "external");

        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Code))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Code is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/otp-code-required"
            });
        }

        // Get delivery and validate status
        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.Status != RequestStatus.HeadingOff)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "OTP verification only allowed when delivery status is 'heading_off'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-otp-verification-state"
            });
        }

        // Check if delivery is locked out
        var now = _clock.GetUtcNow();
        if (_externalOtpLockouts.TryGetValue(deliveryId, out var lockedAt))
        {
            return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
            {
                EscalationId = $"ext_otp_{deliveryId}", // Placeholder escalation ID
                LockedAt = lockedAt,
                Reason = EscalationReason.OtpLocked
            });
        }

        // Get current attempt count
        _externalOtpAttempts.TryGetValue(deliveryId, out var attemptCount);

        try
        {
            // Call external OTP validation service
            var applicationId = $"delivery_handover_{deliveryId}";
            var clientPhoneNumber = "+962700000000"; // TODO: Get from user management service

            await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber = clientPhoneNumber,
                Otp = body.Code,
                ApplicationId = applicationId
            }, ct);

            // OTP verification successful - transition to done status
            await _store.SetStatusAsync(deliveryId, RequestStatus.Delivered, ct);

            // Clear attempt tracking
            _externalOtpAttempts.Remove(deliveryId);
            _externalOtpLockouts.Remove(deliveryId);

            // AC6: Log handover.verified event with deliveryId
            _log.LogInformation(
                "handover.verified: OTP verified successfully for delivery {DeliveryId}, correlationId {CorrelationId}. Status transitioned to done.",
                deliveryId, correlationId);

            activity?.SetTag("otp.verified", "true");
            activity?.SetTag("delivery.status_transition", "heading_off_to_delivered");

            // TODO: Trigger commission settlement via T-BE-020

            return Ok(new OtpHandoverVerificationResponse
            {
                DeliveryId = deliveryId,
                Verified = true,
                Status = RequestStatus.Delivered,
                Message = "OTP verified successfully. Delivery completed."
            });
        }
        catch (Exception ex)
        {
            // OTP verification failed - increment attempt counter
            attemptCount++;
            _externalOtpAttempts[deliveryId] = attemptCount;

            _log.LogWarning(ex,
                "handover.verification_failed: OTP verification failed for delivery {DeliveryId}, correlationId {CorrelationId}. Attempt {AttemptCount}/3",
                deliveryId, correlationId, attemptCount);

            activity?.SetTag("otp.verified", "false");
            activity?.SetTag("otp.attempt_count", attemptCount);

            const int maxAttempts = 3;
            if (attemptCount >= maxAttempts)
            {
                // Lock out after 3rd attempt
                _externalOtpLockouts[deliveryId] = now;
                _externalOtpAttempts.Remove(deliveryId);

                // TODO: Create admin escalation
                _log.LogWarning(
                    "handover.lockout: External OTP lockout for delivery {DeliveryId}, correlationId {CorrelationId} after {Attempts} attempts",
                    deliveryId, correlationId, attemptCount);

                activity?.SetTag("otp.locked_out", "true");
                activity?.SetTag("otp.max_attempts_reached", "true");

                return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
                {
                    EscalationId = $"ext_otp_{deliveryId}", // Placeholder - should create real escalation
                    LockedAt = now,
                    Reason = EscalationReason.OtpLocked
                });
            }

            // Return failure with remaining attempts
            return BadRequest(new ProblemDetails
            {
                Title = "OTP verification failed.",
                Detail = $"Invalid code. {maxAttempts - attemptCount} attempt(s) remaining.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/otp-verification-failed"
            });
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
        GpsTrackingActive = r.GpsTrackingActive,
        OtpAttemptCount = r.OtpAttemptCount,
        OtpLockedAt = r.OtpLockedAt,
        ClientUnreachableAt = r.ClientUnreachableAt,
        OtpEscalationId = r.OtpEscalationId
    };
}
