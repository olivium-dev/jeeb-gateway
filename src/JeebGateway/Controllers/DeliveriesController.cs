using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Text;

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
    private readonly IDeliveryServiceClient _deliveryClient;
    private readonly IDistributedCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeliveriesController> _log;

    // T-BE-019 (JEB-55): external-OTP attempt + lockout TTLs. 15 min on
    // both: long enough to cover the handover window, short enough that
    // a stuck lockout self-heals after the courier moves on. Production
    // tuning lives in OtpHandoverOptions when we promote these to config.
    private static readonly TimeSpan ExternalOtpAttemptsTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExternalOtpLockoutTtl = TimeSpan.FromMinutes(15);

    private static string AttemptsCacheKey(string deliveryId) => $"otp:attempts:{deliveryId}";
    private static string LockoutCacheKey(string deliveryId)  => $"otp:lockout:{deliveryId}";

    public DeliveriesController(
        IRequestsStore store,
        IPushNotificationService push,
        ICancellationService cancellations,
        IAdminEscalationStore escalations,
        IOptions<OtpHandoverOptions> otpOptions,
        IServiceOTPClient otpClient,
        IDeliveryServiceClient deliveryClient,
        IDistributedCache cache,
        TimeProvider clock,
        ILogger<DeliveriesController> log)
    {
        _store = store;
        _push = push;
        _cancellations = cancellations;
        _escalations = escalations;
        _otpOptions = otpOptions;
        _otpClient = otpClient;
        _deliveryClient = deliveryClient;
        _cache = cache;
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
    /// ApplicationId pattern: <c>delivery_handover_{deliveryId}</c>.
    /// Only valid when delivery status = <see cref="RequestStatus.AtDoor"/> —
    /// the Jeeber must have physically arrived at the drop-off before an
    /// OTP is dispatched (PR review B1; per AC1).
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

        // PR review B1 (JEB-628): AC1 requires status `at_door` (the
        // handover step), not `heading_off` (the en-route step). Issuing
        // an OTP before the courier has arrived is the wrong UX.
        if (delivery.Status != RequestStatus.AtDoor)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "OTP can only be triggered when delivery status is 'at_door'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-otp-trigger-state"
            });
        }

        // PR review B6 (JEB-628): the recipient phone must come from the
        // delivery row, not a hardcoded placeholder. Reject the request
        // when the field is unset so production traffic never silently
        // ships OTPs to a placeholder Jordanian number.
        if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            _log.LogWarning(
                "OTP trigger rejected: recipient phone missing for delivery {DeliveryId}, correlationId {CorrelationId}",
                deliveryId, correlationId);
            return BadRequest(new ProblemDetails
            {
                Title = "Recipient phone is missing on the delivery row.",
                Detail = "An OTP cannot be dispatched without a recipient phone number.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/recipient-phone-missing"
            });
        }

        var recipientPhone = delivery.RecipientPhone;
        var applicationId  = $"delivery_handover_{deliveryId}";

        try
        {
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber   = recipientPhone,
                ApplicationId = applicationId
            }, ct);

            // PR review B5: never log the upstream message body — it may
            // echo OTP-adjacent data. Log only safe metadata.
            _log.LogInformation(
                "Handover OTP triggered for delivery {DeliveryId} with applicationId {ApplicationId}, correlationId {CorrelationId}",
                deliveryId, applicationId, correlationId);

            activity?.SetTag("otp.triggered", "true");
            activity?.SetTag("otp.application_id", applicationId);

            return Ok(new OtpTriggerResponse
            {
                DeliveryId = deliveryId,
                Triggered  = true,
                Message    = "4-digit OTP sent to the delivery recipient."
            });
        }
        catch (ApiException apiEx)
        {
            // PR review B5: do NOT pass apiEx (or its Message) to ILogger —
            // ApiException.Message embeds the upstream response body, which
            // may contain the submitted code. Log only StatusCode + a
            // sanitized marker.
            _log.LogWarning(
                "Handover OTP trigger upstream failure for delivery {DeliveryId}: upstream status {UpstreamStatus}, correlationId {CorrelationId}",
                deliveryId, apiEx.StatusCode, correlationId);

            return Problem(
                title:      "Failed to send OTP",
                detail:     "Unable to trigger OTP via the one-time-password service.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            // Non-ApiException: a network/timeout/cancellation failure
            // before we even reached the upstream. Safe to log the type
            // and message — no upstream body is involved.
            _log.LogError(
                "Handover OTP trigger failed for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                deliveryId, ex.GetType().Name, correlationId);

            return Problem(
                title:      "Failed to send OTP",
                detail:     "Unable to trigger OTP via the one-time-password service.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /v1/deliveries/{id}/otp/verify (T-BE-019 / JEB-55).
    ///
    /// Verifies a 4-digit OTP against the external one-time-password service.
    /// On success: delegates the status transition to delivery-service so the
    /// canonical record is authoritative (commission settlement keys off it).
    /// On failure: increments a shared-cache attempt counter; returns 401 on a
    /// plain wrong code (PR review B2 / AC3) and 423 after the third failure,
    /// at which point a real admin-escalation row is created via
    /// <see cref="IAdminEscalationStore"/> (PR review B7).
    /// </summary>
    [HttpPost("{deliveryId}/otp/verify")]
    [ProducesResponseType(typeof(OtpHandoverVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
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
                Title  = "Code is required.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/otp-code-required"
            });
        }

        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        // PR review B1: handover OTP applies at the `at_door` step.
        if (delivery.Status != RequestStatus.AtDoor)
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "OTP verification only allowed when delivery status is 'at_door'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/invalid-otp-verification-state"
            });
        }

        // PR review B6: recipient phone must come from the row, not a placeholder.
        if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            _log.LogWarning(
                "OTP verify rejected: recipient phone missing for delivery {DeliveryId}, correlationId {CorrelationId}",
                deliveryId, correlationId);
            return BadRequest(new ProblemDetails
            {
                Title  = "Recipient phone is missing on the delivery row.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/recipient-phone-missing"
            });
        }

        // PR review B4: cross-replica safe — IDistributedCache (Redis in prod,
        // in-memory in tests) replaces the static Dictionary. Lockout has a
        // TTL so a stuck row self-heals.
        var now = _clock.GetUtcNow();
        var existingLockout = await _cache.GetAsync(LockoutCacheKey(deliveryId), ct);
        if (existingLockout is not null)
        {
            // Re-surface the prior escalation if it exists, otherwise return
            // the bare lockout marker (the escalation might have been opened
            // by a sweeper or by this controller earlier in the lockout TTL).
            var prior = await _escalations.GetForDeliveryAsync(deliveryId, EscalationReason.OtpLocked, ct);
            return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
            {
                EscalationId = prior?.Id ?? string.Empty,
                LockedAt     = DecodeLockoutTimestamp(existingLockout) ?? now,
                Reason       = EscalationReason.OtpLocked
            });
        }

        var attemptCount = await ReadAttemptCountAsync(deliveryId, ct);
        var applicationId = $"delivery_handover_{deliveryId}";

        bool verified;
        int upstreamStatus = 0;
        try
        {
            await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber   = delivery.RecipientPhone,
                Otp           = body.Code,
                ApplicationId = applicationId
            }, ct);
            verified = true;
        }
        catch (ApiException apiEx)
        {
            // PR review B5: NEVER log apiEx / apiEx.Message — the NSwag
            // ApiException embeds the upstream response body in Message,
            // which may echo the submitted code or other OTP-adjacent data.
            // Log only the upstream HTTP status.
            verified       = false;
            upstreamStatus = apiEx.StatusCode;
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled (caller disconnect / shutdown). Surface
            // 499-equivalent via the framework default by rethrowing.
            throw;
        }
        catch (Exception ex)
        {
            // Network / timeout failure before reaching upstream — safe to
            // log the exception type but NOT the message (defense in depth).
            _log.LogWarning(
                "Handover OTP verify pre-upstream failure for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                deliveryId, ex.GetType().Name, correlationId);
            return Problem(
                title:      "OTP verification failed",
                detail:     "Unable to reach the one-time-password service.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        if (verified)
        {
            // PR review B3 + AC2: the canonical state-machine writer is the
            // upstream delivery-service. The gateway hands the transition
            // off so commission settlement (T-BE-020) keys off the same
            // record. Local store mirrors the flip only after the upstream
            // call succeeds.
            try
            {
                await _deliveryClient.StatusTransitionAsync(deliveryId, RequestStatus.Delivered, ct);
            }
            catch (ApiException apiEx)
            {
                _log.LogError(
                    "Upstream status transition failed after successful OTP verify for delivery {DeliveryId}: upstream status {UpstreamStatus}, correlationId {CorrelationId}",
                    deliveryId, apiEx.StatusCode, correlationId);
                return Problem(
                    title:      "OTP verified but status transition failed",
                    detail:     "Please retry; the OTP remains valid.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (HttpRequestException hreq)
            {
                _log.LogError(
                    "Upstream status transition network failure after successful OTP verify for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                    deliveryId, hreq.GetType().Name, correlationId);
                return Problem(
                    title:      "OTP verified but status transition failed",
                    detail:     "Please retry; the OTP remains valid.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            // Mirror the canonical transition in the gateway's read-cache so
            // subsequent GETs do not show stale state. The upstream write is
            // already canonical — this is a best-effort local sync.
            await _store.SetStatusAsync(deliveryId, RequestStatus.Delivered, ct);

            // Clear the attempt + lockout markers (no-op if absent).
            await _cache.RemoveAsync(AttemptsCacheKey(deliveryId), ct);
            await _cache.RemoveAsync(LockoutCacheKey(deliveryId), ct);

            // AC6: emit the canonical handover.verified event. No request
            // body, no exception messages — only the delivery id and a
            // correlation id so on-call can join against APM traces.
            _log.LogInformation(
                "handover.verified deliveryId={DeliveryId} correlationId={CorrelationId} status=delivered",
                deliveryId, correlationId);

            activity?.SetTag("otp.verified", "true");
            activity?.SetTag("delivery.status_transition", "at_door_to_delivered");

            return Ok(new OtpHandoverVerificationResponse
            {
                DeliveryId = deliveryId,
                Verified   = true,
                Status     = RequestStatus.Delivered,
                Message    = "OTP verified successfully. Delivery completed."
            });
        }

        // ---- wrong-code branch -------------------------------------------------

        attemptCount++;
        await WriteAttemptCountAsync(deliveryId, attemptCount, ct);

        _log.LogWarning(
            "handover.verification_failed deliveryId={DeliveryId} correlationId={CorrelationId} attempt={Attempt}/{Max} upstreamStatus={UpstreamStatus}",
            deliveryId, correlationId, attemptCount, _otpOptions.Value.MaxAttempts, upstreamStatus);

        activity?.SetTag("otp.verified", "false");
        activity?.SetTag("otp.attempt_count", attemptCount);

        var maxAttempts = _otpOptions.Value.MaxAttempts;
        if (attemptCount >= maxAttempts)
        {
            // Persist the lockout flag with the timestamp so subsequent
            // requests can read the locked-at moment back.
            await _cache.SetAsync(
                LockoutCacheKey(deliveryId),
                EncodeLockoutTimestamp(now),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ExternalOtpLockoutTtl },
                ct);
            await _cache.RemoveAsync(AttemptsCacheKey(deliveryId), ct);

            // PR review B7: real admin escalation row — surfaces in the
            // moderation queue so ops can triage stuck handovers.
            var escalation = await _escalations.CreateAsync(new AdminEscalation
            {
                Id              = Guid.NewGuid().ToString(),
                DeliveryId      = deliveryId,
                ClientId        = delivery.ClientId,
                JeeberId        = delivery.JeeberId,
                Reason          = EscalationReason.OtpLocked,
                Status          = EscalationStatus.Pending,
                CreatedAt       = now,
                OtpAttemptCount = attemptCount,
            }, ct);

            _log.LogWarning(
                "handover.lockout deliveryId={DeliveryId} correlationId={CorrelationId} attempts={Attempts} escalationId={EscalationId}",
                deliveryId, correlationId, attemptCount, escalation.Id);

            activity?.SetTag("otp.locked_out", "true");
            activity?.SetTag("otp.max_attempts_reached", "true");
            activity?.SetTag("otp.escalation_id", escalation.Id);

            return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
            {
                EscalationId = escalation.Id,
                LockedAt     = now,
                Reason       = EscalationReason.OtpLocked
            });
        }

        // PR review B2 / AC3: wrong code is HTTP 401 (Unauthorized), not 400.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new ObjectResult(new ProblemDetails
        {
            Title  = "OTP verification failed.",
            Detail = $"Invalid code. {maxAttempts - attemptCount} attempt(s) remaining.",
            Status = StatusCodes.Status401Unauthorized,
            Type   = "https://jeeb.dev/errors/otp-verification-failed"
        })
        {
            StatusCode  = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/problem+json" }
        };
    }

    // ---- IDistributedCache helpers for the OTP attempt counter --------------

    private async Task<int> ReadAttemptCountAsync(string deliveryId, CancellationToken ct)
    {
        var bytes = await _cache.GetAsync(AttemptsCacheKey(deliveryId), ct);
        if (bytes is null || bytes.Length == 0) return 0;
        return int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private Task WriteAttemptCountAsync(string deliveryId, int count, CancellationToken ct)
        => _cache.SetAsync(
            AttemptsCacheKey(deliveryId),
            Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ExternalOtpAttemptsTtl },
            ct);

    private static byte[] EncodeLockoutTimestamp(DateTimeOffset at)
        => Encoding.UTF8.GetBytes(at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    private static DateTimeOffset? DecodeLockoutTimestamp(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        return long.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
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
