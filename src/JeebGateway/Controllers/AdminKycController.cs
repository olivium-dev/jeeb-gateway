using JeebGateway.Admin;
using JeebGateway.Kyc;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-005 / JEEB-23: admin KYC moderation queue.
///
/// GET /admin/kyc/queue paginates submissions still in
/// <see cref="KycStatus.PendingReview"/> ordered by submission time so
/// reviewers drain the queue oldest-first.
///
/// PATCH /admin/kyc/{id}/review accepts one of three actions:
///   * approve — flips the row to <see cref="KycStatus.Verified"/> and
///     unlocks the <see cref="Roles.Jeeber"/> role on the user within
///     5 seconds (AC #2). A confirmation push fires best-effort.
///   * reject — flips the row to <see cref="KycStatus.Rejected"/>,
///     stores the reason, and pushes the reason to the user so they
///     know they can resubmit (AC #3).
///   * request_resubmit — flips the row to
///     <see cref="KycStatus.ResubmitRequested"/> and stores the subset
///     of document steps the user must re-upload. The mobile app uses
///     that subset to reopen only those steps (AC #4).
///
/// Every action lands an entry in <see cref="IAdminAuditLog"/>.
/// </summary>
[ApiController]
[Route("admin/kyc")]
[RequireRole(Roles.Admin)]
public class AdminKycController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxReasonLength = 500;

    private const string EntityType = "kyc_submission";
    private const string ActionApprove = "approve_kyc";
    private const string ActionReject = "reject_kyc";
    private const string ActionRequestResubmit = "request_resubmit_kyc";

    private readonly IKycService _service;
    private readonly IKycStore _store;
    private readonly IAdminAuditLog _auditLog;

    public AdminKycController(IKycService service, IKycStore store, IAdminAuditLog auditLog)
    {
        _service = service;
        _store = store;
        _auditLog = auditLog;
    }

    [HttpGet("queue")]
    [ProducesResponseType(typeof(KycQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Queue(
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

        var queue = await _store.ListPendingForReviewAsync(page, pageSize, ct);

        return Ok(new KycQueueResponse
        {
            Items = queue.Items.Select(ToQueueItem).ToList(),
            Page = queue.Page,
            PageSize = queue.PageSize,
            Total = queue.Total
        });
    }

    [HttpPatch("{id}/review")]
    [ProducesResponseType(typeof(KycReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Review(string id, [FromBody] KycReviewRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!TryParseAction(body.Action, out var action, out var actionError))
        {
            return BadRequest(actionError);
        }

        var reason = body.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"reason must be {MaxReasonLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var before = await _store.GetByIdAsync(id, ct);
        if (before is null) return NotFound();

        KycReviewOutcome? outcome;
        try
        {
            outcome = await _service.ReviewAsync(id, new KycReviewInput
            {
                Action = action,
                ReviewerId = adminId,
                Reason = reason,
                ResubmitSteps = body.ResubmitSteps
            }, ct);
        }
        catch (KycReviewValidationException ex)
        {
            // Validation failures from the service layer are user-input
            // problems (missing reason, unknown step name, already-reviewed
            // row). Map the "already reviewed" case to 409 so the admin UI
            // can distinguish stale state from a bad request.
            var status = ex.Message.Contains("no longer pending", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return StatusCode(status, new ProblemDetails
            {
                Title = ex.Message,
                Status = status
            });
        }

        if (outcome is null) return NotFound();

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = AuditActionFor(action),
            EntityType = EntityType,
            EntityId = id,
            BeforeState = Snapshot(before),
            AfterState = Snapshot(outcome.Submission),
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        return Ok(new KycReviewResponse
        {
            Submission = ToResponse(outcome.Submission),
            RoleGranted = outcome.RoleGranted,
            PushSent = outcome.PushSent
        });
    }

    private static bool TryParseAction(string? raw, out KycReviewAction action, out ProblemDetails error)
    {
        action = default;
        error = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = new ProblemDetails
            {
                Title = "action is required (approve, reject, or request_resubmit).",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "approve":
                action = KycReviewAction.Approve;
                return true;
            case "reject":
                action = KycReviewAction.Reject;
                return true;
            case "request_resubmit":
            case "resubmit":
                action = KycReviewAction.RequestResubmit;
                return true;
            default:
                error = new ProblemDetails
                {
                    Title = $"Unknown action '{raw}'. Allowed: approve, reject, request_resubmit.",
                    Status = StatusCodes.Status400BadRequest
                };
                return false;
        }
    }

    private static string AuditActionFor(KycReviewAction action) => action switch
    {
        KycReviewAction.Approve => ActionApprove,
        KycReviewAction.Reject => ActionReject,
        KycReviewAction.RequestResubmit => ActionRequestResubmit,
        _ => action.ToString()
    };

    private static IReadOnlyDictionary<string, object?> Snapshot(KycSubmission s) =>
        new Dictionary<string, object?>
        {
            ["status"] = s.Status,
            ["reviewed_at"] = s.ReviewedAt,
            ["reviewer_id"] = s.ReviewerId,
            ["rejection_reason"] = s.RejectionReason,
            ["resubmit_steps"] = s.ResubmitSteps.ToList()
        };

    private static KycQueueItem ToQueueItem(KycSubmission s) => new()
    {
        Id = s.Id,
        UserId = s.UserId,
        Status = s.Status,
        SubmittedAt = s.SubmittedAt,
        VehicleType = s.VehicleType,
        VehicleRegistration = s.VehicleRegistration,
        LivenessPassed = s.LivenessPassed
    };

    private static KycSubmissionResponse ToResponse(KycSubmission s) => new()
    {
        Id = s.Id,
        UserId = s.UserId,
        Status = s.Status,
        SubmittedAt = s.SubmittedAt,
        ReviewedAt = s.ReviewedAt,
        RejectionReason = s.RejectionReason,
        VehicleType = s.VehicleType,
        VehicleRegistration = s.VehicleRegistration,
        LivenessPassed = s.LivenessPassed,
        ResubmitSteps = s.ResubmitSteps.ToList()
    };
}
