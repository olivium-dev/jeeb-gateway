using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Kyc;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

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
/// (ARCH LAW). That composition lives in <see cref="KycAdminReviewComposer"/>, shared
/// verbatim with the CMS-compat review route so the two can never fork.</para>
///
/// <list type="bullet">
///   <item>GET <c>/admin/kyc/queue</c> — pending submissions oldest-first (H7/N6),
///     optionally filtered by <c>?q=</c> on the applicant's name/phone.</item>
///   <item>PATCH <c>/admin/kyc/{id}/review</c> — approve | reject | request_resubmit;
///     re-review of a finalised row → 409 (N8); RFC7807 throughout.</item>
/// </list>
/// </summary>
[ApiController]
[Route("admin/kyc")]
// ADR-005 L2: both the queue read and the review decision are the same admin capability
// (kyc.review), declared class-level (replaces class [RequireRole(Roles.Admin)]). Authorized
// purely from the 'admin' role claim. The KYC-approve identity mutation (UM append + token
// re-issue) is a downstream/STATE concern and stays unchanged in the action body.
public class AdminKycController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly KycQueueSearch _queue;
    private readonly KycAdminReviewComposer _reviews;

    public AdminKycController(KycQueueSearch queue, KycAdminReviewComposer reviews)
    {
        _queue = queue;
        _reviews = reviews;
    }

    [HttpGet("queue")]
    [RequireCapability(Capabilities.KycReview)]
    [ProducesResponseType(typeof(KycQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Queue(
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

        KycQueueSearchPage queue;
        try
        {
            queue = await _queue.SearchAsync(page, pageSize, q, ct);
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
    [RequireCapability(Capabilities.KycReview)]
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

        if (!KycAdminReviewComposer.TryParseAction(body.Action, out var action, out var actionError))
        {
            return BadRequest(new ProblemDetails
            {
                Title = actionError,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var outcome = await _reviews.ReviewAsync(
            id, action, adminId, body.Reason, body.ResubmitSteps, HttpContext.TraceIdentifier, ct);

        if (outcome.Status != KycAdminReviewStatus.Ok)
        {
            return MapFailure(outcome);
        }

        return Ok(new KycReviewResponse
        {
            Submission = ToResponse(outcome.Result!),
            RoleGranted = outcome.RoleGranted,
            // Interim path delivers the status push inline; upstream path composes
            // notification async off the critical path (N14).
            PushSent = outcome.Result!.PushSent
        });
    }

    /// <summary>
    /// Shared failure translation for both review routes — same statuses, same RFC 7807
    /// shapes the native route has always emitted.
    /// </summary>
    internal IActionResult MapFailure(KycAdminReviewOutcome outcome) => outcome.Status switch
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
            // JEB-1472 / AC3: the {client,jeeber} whitelist rejects an unknown Jeeb contract
            // role at the gateway boundary; a non-contract role never reaches shared UM.
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

    internal IActionResult KycUpstreamDisabled() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/upstream-unavailable",
            Title = "KYC upstream unavailable",
            Detail = "The KYC service is not enabled.",
            Status = StatusCodes.Status503ServiceUnavailable
        });

    private static KycQueueItem ToQueueItem(KycQueueSearchRow row) => new()
    {
        Id = row.Item.SubmissionId,
        UserId = row.Item.UserId,
        Status = row.Item.Status,
        SubmittedAt = row.Item.SubmittedAt,
        UserName = row.UserName,
        Phone = row.Phone,
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
