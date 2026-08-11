using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Kyc;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// CMS-compat KYC admin surface — the routes the DEPLOYED back-office bundle actually calls.
///
/// <para><b>Why this controller exists.</b> The shipped KYC micro-frontend
/// (<c>ofl-cms-kyc-mfe</c>, byte-verified in the live release's <c>mf/kyc/25.js</c>) issues
/// <c>GET|PATCH &lt;origin&gt;/gateway/user-management/admin/kyc[...]</c>. The back-office vhost
/// strips exactly one <c>/gateway/</c>, so the gateway must serve <c>user-management/admin/kyc</c>.
/// It served nothing there — every CMS KYC screen failed at the LIST call. This is the same
/// compat-facade pattern as the legacy alias on <see cref="CmsAuthoringController"/>: the gateway
/// serves the path the deployed bundle already emits, so no CMS redeploy is needed.
/// The path segment is a CMS URL namespace, not a proxy — nothing here calls user-management
/// except the shared role-grant composition on approve.</para>
///
/// <para><b>Honest limits (documented, not hidden).</b>
/// <list type="bullet">
///   <item>kyc-service can only LIST the pending-review queue (its list API takes
///     status/page/pageSize and returns the same queue for any other value), so
///     <c>status=approved|rejected</c> returns an empty page rather than a wrong one.</item>
///   <item><c>q</c> matches name/phone from the GATEWAY's user projection over a bounded
///     pending window (<see cref="KycQueueSearch.MaxWindowRows"/> rows) — kyc-service rows
///     carry a subject id only.</item>
///   <item><c>templateId</c> is the constant Jeeb jeeber template: kyc-service models no
///     template id anywhere.</item>
/// </list></para>
/// </summary>
[ApiController]
[Route("user-management/admin/kyc")]
[RequireCapability(Capabilities.KycReview)]
public sealed class CmsKycAdminController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>The one Jeeb KYC template; kyc-service carries no template identity.</summary>
    private const string JeebKycTemplateId = "jeeb_jeeber_v1";

    private const string StatusPending = "pending";
    private const string StatusApproved = "approved";
    private const string StatusRejected = "rejected";
    private const string StatusAll = "all";

    private readonly KycQueueSearch _queue;
    private readonly KycAdminReviewComposer _reviews;
    private readonly IKycBffSeam _kyc;
    private readonly IUsersStore _users;
    private readonly ILogger<CmsKycAdminController> _log;

    public CmsKycAdminController(
        KycQueueSearch queue,
        KycAdminReviewComposer reviews,
        IKycBffSeam kyc,
        IUsersStore users,
        ILogger<CmsKycAdminController> log)
    {
        _queue = queue;
        _reviews = reviews;
        _kyc = kyc;
        _users = users;
        _log = log;
    }

    [HttpGet("")]
    [ProducesResponseType(typeof(KycAdminPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? status = StatusPending,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? q = null,
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

        var wanted = string.IsNullOrWhiteSpace(status) ? StatusPending : status.Trim().ToLowerInvariant();
        if (wanted is not (StatusPending or StatusApproved or StatusRejected or StatusAll))
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"Unknown status '{status}'. Allowed: pending, approved, rejected, all.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // kyc-service exposes ONLY the pending-review queue. Answering approved/rejected from
        // the pending queue would be a lie, so those filters return an honest empty page.
        if (wanted is StatusApproved or StatusRejected)
        {
            return Ok(new KycAdminPage
            {
                Items = Array.Empty<KycAdminListItem>(),
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0
            });
        }

        KycQueueSearchPage queue;
        try
        {
            queue = await _queue.SearchAsync(page, pageSize, q, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        if (queue.WindowTruncated)
        {
            _log.LogWarning(
                "cms kyc list: q search window capped at {Rows} pending rows; results are partial",
                KycQueueSearch.MaxWindowRows);
        }

        return Ok(new KycAdminPage
        {
            Items = queue.Items.Select(ToListItem).ToList(),
            Page = queue.Page,
            PageSize = queue.PageSize,
            TotalCount = queue.Total,
            TotalPages = queue.PageSize <= 0 ? 0 : (int)Math.Ceiling(queue.Total / (double)queue.PageSize)
        });
    }

    [HttpGet("{submissionId}")]
    [ProducesResponseType(typeof(KycAdminDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string submissionId, CancellationToken ct)
    {
        KycBffSubmissionView? view;
        try
        {
            view = await _kyc.GetByIdAsync(submissionId, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        if (view is null) return NotFound();

        return Ok(await ToDetailAsync(
            view.SubmissionId, view.UserId, ToCmsStatus(view.Status),
            view.SubmittedAt, view.ReviewedAt, view.RejectionReason, ct));
    }

    [HttpPatch("{submissionId}")]
    [ProducesResponseType(typeof(KycAdminDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Review(
        string submissionId, [FromBody] KycAdminReviewBody? body, CancellationToken ct)
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

        if (!KycAdminReviewComposer.TryParseAction(body.Action, out var action, out var actionError)
            || action == KycReviewAction.RequestResubmit)
        {
            return BadRequest(new ProblemDetails
            {
                Title = string.IsNullOrEmpty(actionError)
                    ? "action must be approve or reject."
                    : actionError,
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Same composer as PATCH /admin/kyc/{id}/review: identical whitelist, UM role append
        // and audit entry — the review behaviour cannot fork between the two routes.
        var outcome = await _reviews.ReviewAsync(
            submissionId, action, adminId, body.Reason, null, HttpContext.TraceIdentifier, ct);

        if (outcome.Status != KycAdminReviewStatus.Ok)
        {
            return MapFailure(outcome);
        }

        var result = outcome.Result!;
        var reviewedAt = DateTimeOffset.UtcNow;

        // The review result carries no submittedAt; re-read the row so the detail the CMS
        // renders after a decision is the real one, not a fabricated timestamp.
        DateTimeOffset submittedAt = default;
        try
        {
            var view = await _kyc.GetByIdAsync(submissionId, ct);
            if (view is not null) submittedAt = view.SubmittedAt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "cms kyc review {SubmissionId}: post-review re-read failed; submittedAt omitted", submissionId);
        }

        return Ok(await ToDetailAsync(
            result.SubmissionId, result.UserId ?? string.Empty, ToCmsStatus(result.Status),
            submittedAt, reviewedAt, result.RejectionReason, ct));
    }

    private async Task<KycAdminDetail> ToDetailAsync(
        string id, string userId, string status,
        DateTimeOffset submittedAt, DateTimeOffset? reviewedAt, string? reviewReason,
        CancellationToken ct)
    {
        UserProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                profile = await _users.GetByIdAsync(userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "cms kyc detail {SubmissionId}: user projection lookup failed; user summary blank", id);
            }
        }

        return new KycAdminDetail
        {
            Id = id,
            UserId = userId,
            TemplateId = JeebKycTemplateId,
            Status = status,
            SubmittedAt = submittedAt,
            ReviewedAt = reviewedAt,
            ReviewReason = reviewReason,
            User = new KycAdminUserSummary
            {
                Id = userId,
                Name = profile?.Name ?? string.Empty,
                Phone = profile?.Phone ?? string.Empty
            }
        };
    }

    private IActionResult MapFailure(KycAdminReviewOutcome outcome) => outcome.Status switch
    {
        KycAdminReviewStatus.UpstreamDisabled => KycUpstreamDisabled(),
        KycAdminReviewStatus.NotFound => NotFound(),
        KycAdminReviewStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
        {
            Title = outcome.Error,
            Status = StatusCodes.Status409Conflict
        }),
        KycAdminReviewStatus.InvalidRole => BadRequest(new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/invalid-role",
            Title = "invalid_role",
            Detail = outcome.Error,
            Status = StatusCodes.Status400BadRequest
        }),
        _ => BadRequest(new ProblemDetails
        {
            Title = outcome.Error,
            Status = StatusCodes.Status400BadRequest
        })
    };

    private IActionResult KycUpstreamDisabled() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/upstream-unavailable",
            Title = "KYC upstream unavailable",
            Detail = "The KYC service is not enabled.",
            Status = StatusCodes.Status503ServiceUnavailable
        });

    private static KycAdminListItem ToListItem(KycQueueSearchRow row) => new()
    {
        Id = row.Item.SubmissionId,
        UserId = row.Item.UserId,
        UserName = row.UserName,
        Phone = row.Phone,
        TemplateId = JeebKycTemplateId,
        Status = ToCmsStatus(row.Item.Status),
        SubmittedAt = row.Item.SubmittedAt
    };

    /// <summary>kyc-service vocabulary → the CMS contract's {pending,approved,rejected}.</summary>
    private static string ToCmsStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "verified" or "approved" => StatusApproved,
        "rejected" => StatusRejected,
        _ => StatusPending
    };
}

/// <summary>CMS <c>KycReviewRequest</c>: <c>{ action: approve|reject, reason?: string }</c>.</summary>
public sealed class KycAdminReviewBody
{
    public string? Action { get; init; }
    public string? Reason { get; init; }
}

/// <summary>CMS <c>KycAdminListItem</c>.</summary>
public sealed class KycAdminListItem
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public string? UserName { get; init; }
    public string? Phone { get; init; }
    public required string TemplateId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>CMS <c>KycAdminPage</c>.</summary>
public sealed class KycAdminPage
{
    public required IReadOnlyList<KycAdminListItem> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public required int TotalPages { get; init; }
}

/// <summary>CMS <c>KycAdminUserSummary</c>.</summary>
public sealed class KycAdminUserSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Phone { get; init; }
}

/// <summary>CMS <c>KycAdminDetail</c>.</summary>
public sealed class KycAdminDetail
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string TemplateId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? ReviewReason { get; init; }
    public required KycAdminUserSummary User { get; init; }
}
