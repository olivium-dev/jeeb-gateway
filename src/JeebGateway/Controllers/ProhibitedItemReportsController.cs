using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Mobile-facing prohibited-item report intake. Reports reuse the existing
/// flagged-request moderation queue so admins review one queue regardless of
/// whether the signal came from the scanner or a Jeeber report.
/// </summary>
[ApiController]
[Route("prohibited-items/reports")]
[RequireCapability(Capabilities.ProhibitedReport)]
public sealed class ProhibitedItemReportsController : ControllerBase
{
    private const int MaxReasonLength = 4000;

    private readonly IFlaggedRequestStore _flaggedRequests;

    public ProhibitedItemReportsController(IFlaggedRequestStore flaggedRequests)
    {
        _flaggedRequests = flaggedRequests;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ProhibitedItemReportResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody] ProhibitedItemReportRequest body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.RequestId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "requestId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(body.Reason))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "reason is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var reason = body.Reason.Trim();
        if (reason.Length > MaxReasonLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"reason must be {MaxReasonLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var report = await _flaggedRequests.CreateAsync(new FlaggedRequestCreate
        {
            RequestId = body.RequestId.Trim(),
            UserId = userId,
            Description = reason,
            Matches = Array.Empty<JeebGateway.ProhibitedItems.Scanner.ProhibitedItemMatch>()
        }, ct);

        return Accepted(new ProhibitedItemReportResponse
        {
            ReportId = report.Id,
            RequestId = report.RequestId!,
            Status = report.Status.ToString().ToLowerInvariant(),
            CreatedAt = report.CreatedAt
        });
    }
}

public sealed class ProhibitedItemReportRequest
{
    public string? RequestId { get; set; }
    public string? Reason { get; set; }
}

public sealed class ProhibitedItemReportResponse
{
    public required string ReportId { get; init; }
    public required string RequestId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
