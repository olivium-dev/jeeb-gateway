using JeebGateway.Admin;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-024 (JEEB-42): admin moderation queue for Client-requested
/// cancellations that landed after pickup. Mirrors the AdminKyc shape:
/// paginated GET, PATCH per row, every decision lands in
/// <see cref="IAdminAuditLog"/>.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("admin/cancellations")]
[RequireRole(Roles.Admin)]
public class AdminCancellationsController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string EntityType = "delivery_cancellation";
    private const string ActionApprove = "approve_cancellation";
    private const string ActionReject = "reject_cancellation";

    private readonly ICancellationService _cancellations;
    private readonly IRequestsStore _requests;
    private readonly IPushNotificationService _push;
    private readonly IAdminAuditLog _auditLog;
    private readonly ILogger<AdminCancellationsController> _log;

    public AdminCancellationsController(
        ICancellationService cancellations,
        IRequestsStore requests,
        IPushNotificationService push,
        IAdminAuditLog auditLog,
        ILogger<AdminCancellationsController> log)
    {
        _cancellations = cancellations;
        _requests = requests;
        _push = push;
        _auditLog = auditLog;
        _log = log;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminCancellationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "page must be >= 1.",
                Status = StatusCodes.Status400BadRequest
            });
        }
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"pageSize must be between 1 and {MaxPageSize}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var (items, total) = await _cancellations.ListPendingApprovalsAsync(page, pageSize, ct);

        return Ok(new AdminCancellationsResponse
        {
            Items = items.Select(ToItem).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = total
        });
    }

    [HttpPatch("{deliveryId}")]
    [ProducesResponseType(typeof(AdminCancellationDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decide(
        string deliveryId,
        [FromBody] AdminCancellationDecisionBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Action))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "action is required (approve or reject).",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var before = await _requests.GetAsync(deliveryId, ct);
        if (before is null) return NotFound();

        var result = await _cancellations.DecideAsync(deliveryId, body.Action, ct);

        switch (result.Outcome)
        {
            case AdminCancellationDecisionOutcome.NotFound:
                return NotFound();

            case AdminCancellationDecisionOutcome.NotPending:
                return Conflict(new ProblemDetails
                {
                    Title = "Delivery is not awaiting cancellation approval.",
                    Detail = $"current status: {result.Request?.Status}",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/not-pending-cancellation"
                });

            case AdminCancellationDecisionOutcome.UnknownAction:
                return BadRequest(new ProblemDetails
                {
                    Title = $"Unknown action '{body.Action}'. Allowed: approve, reject.",
                    Status = StatusCodes.Status400BadRequest
                });

            case AdminCancellationDecisionOutcome.Approved:
            case AdminCancellationDecisionOutcome.Rejected:
                await _auditLog.AppendAsync(new AdminAuditAppend
                {
                    AdminUserId = adminId,
                    Action = result.Outcome == AdminCancellationDecisionOutcome.Approved
                        ? ActionApprove
                        : ActionReject,
                    EntityType = EntityType,
                    EntityId = deliveryId,
                    BeforeState = Snapshot(before),
                    AfterState = Snapshot(result.Request!),
                    RequestId = HttpContext.TraceIdentifier
                }, ct);

                await NotifyDecisionAsync(result.Request!, result.Outcome, body.Note, ct);

                return Ok(new AdminCancellationDecisionResponse
                {
                    DeliveryId = result.Request!.Id,
                    Action = result.Outcome == AdminCancellationDecisionOutcome.Approved ? "approve" : "reject",
                    Status = result.Request.Status
                });

            default:
                return Problem(
                    title: "Unhandled admin cancellation outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task NotifyDecisionAsync(
        DeliveryRequest req,
        AdminCancellationDecisionOutcome outcome,
        string? note,
        CancellationToken ct)
    {
        var recipients = new List<string> { req.ClientId };
        if (!string.IsNullOrEmpty(req.JeeberId)) recipients.Add(req.JeeberId);

        var approved = outcome == AdminCancellationDecisionOutcome.Approved;
        var data = new Dictionary<string, string>
        {
            ["deliveryId"] = req.Id,
            ["status"] = req.Status,
            ["decision"] = approved ? "approved" : "rejected"
        };
        if (!string.IsNullOrWhiteSpace(note))
        {
            data["note"] = note;
        }

        var title = approved ? "Cancellation approved" : "Cancellation rejected";
        var bodyText = approved
            ? "Your cancellation request was approved."
            : "Your cancellation request was rejected. The delivery is back in progress.";

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
                    IdempotencyKey: $"{req.Id}:{req.Status}:cancel-decision:{userId}");
                await _push.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Cancellation-decision push failed for delivery {DeliveryId} user {UserId}",
                    req.Id, userId);
            }
        }
    }

    private static AdminCancellationItem ToItem(DeliveryRequest r) => new()
    {
        DeliveryId = r.Id,
        ClientId = r.ClientId,
        JeeberId = r.JeeberId,
        PreviousStatus = r.CancellationPreviousStatus ?? r.Status,
        RequestedAt = r.CancellationRequestedAt ?? r.CreatedAt,
        Reason = r.CancellationReason
    };

    private static IReadOnlyDictionary<string, object?> Snapshot(DeliveryRequest r) =>
        new Dictionary<string, object?>
        {
            ["status"] = r.Status,
            ["cancelled_by"] = r.CancelledBy,
            ["cancellation_reason"] = r.CancellationReason,
            ["cancellation_requested_at"] = r.CancellationRequestedAt,
            ["cancellation_approved_at"] = r.CancellationApprovedAt,
            ["cancellation_rejected_at"] = r.CancellationRejectedAt,
            ["cancellation_previous_status"] = r.CancellationPreviousStatus
        };
}
