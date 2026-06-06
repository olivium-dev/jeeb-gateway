using JeebGateway.Admin;
using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-005 / JEEB-23 / S03 H7-H8 / ADR-0004: admin KYC moderation queue +
/// review. This controller is a THIN BFF over the KYC domain seam
/// (<see cref="IKycBffSeam"/>): it composes the KYC review DECISION (kyc-service
/// when live, interim store while the Kyc flag is off) with the identity mutation
/// in user-management. It holds NO KYC state itself.
///
/// <para><b>The only identity-mutating transition (CP-C / H8).</b> On
/// <c>approve</c> the seam returns the role-grant INTENT
/// (<see cref="KycBffReviewResult.GrantsRole"/> = the opaque jeeber role); the
/// GATEWAY then composes the user-management append (jsonb <c>available_roles</c>,
/// set-semantics) + token re-issue. kyc-service NEVER calls user-management
/// (ARCH LAW). The approve commits regardless of the notification fan-out
/// (off-path, N14) — an approve is never rolled back by a push failure.</para>
///
/// <list type="bullet">
///   <item>GET <c>/admin/kyc/queue</c> — pending submissions oldest-first (H7/N6).</item>
///   <item>PATCH <c>/admin/kyc/{id}/review</c> — approve | reject | request_resubmit;
///     re-review of a finalised row → 409 (N8); RFC7807 throughout.</item>
/// </list>
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

    private readonly IKycBffSeam _kyc;
    private readonly IUserManagementDualRoleClient _userManagement;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IAdminAuditLog _auditLog;
    private readonly ILogger<AdminKycController> _log;

    public AdminKycController(
        IKycBffSeam kyc,
        IUserManagementDualRoleClient userManagement,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IAdminAuditLog auditLog,
        ILogger<AdminKycController> log)
    {
        _kyc = kyc;
        _userManagement = userManagement;
        _flags = flags;
        _auditLog = auditLog;
        _log = log;
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

        KycBffQueuePage queue;
        try
        {
            queue = await _kyc.GetPendingQueueAsync(page, pageSize, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

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

        KycBffReviewResult outcome;
        try
        {
            outcome = await _kyc.ReviewAsync(id, new KycBffReviewInput
            {
                Action = action,
                ReviewerId = adminId,
                Reason = reason,
                ResubmitSteps = body.ResubmitSteps
            }, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }
        catch (KycBffNotFoundException)
        {
            return NotFound();
        }
        catch (KycBffReviewConflictException ex)
        {
            // N8: re-review of a finalised submission.
            return StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
        catch (KycBffReviewValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }

        // CP-C / H8: the ONLY identity-mutating transition. On an approve outcome
        // the seam returns the role-grant INTENT; the GATEWAY composes the UM
        // append (kyc-service never calls UM). The approve already committed in the
        // KYC domain, so a UM blip must not roll it back — we surface roleGranted
        // = false and log, rather than failing the review.
        var roleGranted = false;
        if (!string.IsNullOrWhiteSpace(outcome.GrantsRole))
        {
            roleGranted = await ComposeRoleGrantAsync(outcome.SubmissionId, outcome.UserId, outcome.GrantsRole!, ct);
        }

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = AuditActionFor(action),
            EntityType = EntityType,
            EntityId = id,
            BeforeState = new Dictionary<string, object?> { ["status"] = "pending_review" },
            AfterState = Snapshot(outcome, roleGranted),
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        return Ok(new KycReviewResponse
        {
            Submission = ToResponse(outcome),
            RoleGranted = roleGranted,
            // Interim path delivers the status push inline; upstream path composes
            // notification async off the critical path (N14).
            PushSent = outcome.PushSent
        });
    }

    /// <summary>
    /// Composes the user-management role append for an approve outcome. When the
    /// UserManagement upstream is enabled the gateway calls UM (the durable,
    /// blast-radius-1 jsonb append + token re-issue authority). When it is off,
    /// the interim seam has already granted the role on the in-gateway store, so
    /// the grant is reported as effected. Returns whether the role is now held.
    ///
    /// <para><b>N14 translation (the S03 403 root-fix).</b> The kyc grant INTENT is
    /// the frozen Jeeb CONTRACT role (e.g. <c>jeeber</c>) — that is the only
    /// vocabulary the KYC domain knows. user-management stores OPAQUE roles
    /// (<c>{customer,driver}</c>) and a later <c>role/switch</c> translates
    /// <c>jeeber → driver</c> before checking <c>available_roles</c>. So the append
    /// MUST also be translated to opaque here, or it would store the literal
    /// <c>jeeber</c> while the switch looks for <c>driver</c> → 403. The gateway is
    /// the sole translation seam (UM never names client/jeeber). A non-contract grant
    /// role (unlikely) passes through verbatim.</para>
    /// </summary>
    private async Task<bool> ComposeRoleGrantAsync(
        string submissionId, string? subjectUserId, string contractRole, CancellationToken ct)
    {
        if (!_flags.CurrentValue.UserManagement)
        {
            // Interim path: the in-gateway service already appended the role.
            return true;
        }

        if (string.IsNullOrWhiteSpace(subjectUserId))
        {
            _log.LogWarning(
                "kyc approve {SubmissionId}: review outcome carried no owner; role grant skipped", submissionId);
            return false;
        }

        // Translate the CONTRACT grant role to the OPAQUE role UM persists, so the
        // appended role matches what role/switch will look up (jeeber → driver).
        var opaqueRole = JeebRoleTranslator.ToOpaque(contractRole) ?? contractRole;

        try
        {
            var grant = await _userManagement.AppendAvailableRoleAsync(subjectUserId, opaqueRole, ct);
            return grant.Added || grant.AvailableRoles.Any(
                r => string.Equals(r, opaqueRole, StringComparison.OrdinalIgnoreCase));
        }
        catch (UserManagementCallException ex)
        {
            // Approve never rolls back on a UM blip (N14); surface false + log.
            _log.LogWarning(ex,
                "kyc approve {SubmissionId}: user-management role append failed (status {Status}); "
                + "approve committed, role grant deferred", submissionId, ex.StatusCode);
            return false;
        }
    }

    private IActionResult KycUpstreamDisabled() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/upstream-unavailable",
            Title = "KYC upstream unavailable",
            Detail = "The KYC service is not enabled.",
            Status = StatusCodes.Status503ServiceUnavailable
        });

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

    private static IReadOnlyDictionary<string, object?> Snapshot(KycBffReviewResult r, bool roleGranted) =>
        new Dictionary<string, object?>
        {
            ["status"] = r.Status,
            ["rejection_reason"] = r.RejectionReason,
            ["resubmit_steps"] = r.ResubmitSteps.ToList(),
            ["role_granted"] = roleGranted
        };

    private static KycQueueItem ToQueueItem(KycBffQueueItem s) => new()
    {
        Id = s.SubmissionId,
        UserId = s.UserId,
        Status = s.Status,
        SubmittedAt = s.SubmittedAt,
        // Vehicle metadata is not carried on the thin queue projection (kyc-service
        // owns the full submission); the admin list shows the lifecycle fields.
        VehicleType = string.Empty,
        VehicleRegistration = string.Empty,
        LivenessPassed = false
    };

    private static KycSubmissionResponse ToResponse(KycBffReviewResult r) => new()
    {
        Id = r.SubmissionId,
        UserId = string.Empty,
        Status = r.Status,
        SubmittedAt = default,
        ReviewedAt = DateTimeOffset.UtcNow,
        RejectionReason = r.RejectionReason,
        VehicleType = string.Empty,
        VehicleRegistration = string.Empty,
        LivenessPassed = false,
        ResubmitSteps = r.ResubmitSteps.ToList()
    };
}
