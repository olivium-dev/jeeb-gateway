using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-048 — NLP scan of a delivery request description against the
/// active prohibited-items catalog. Matches above the review threshold are
/// recorded in <see cref="IFlaggedRequestStore"/> so admins can clear or
/// uphold them. The endpoint NEVER auto-blocks the underlying request; that
/// decision lives in the human-in-the-loop admin queue.
/// </summary>
[ApiController]
[Route("prohibited-items")]
public class ProhibitedItemsScanController : ControllerBase
{
    private const int MaxDescriptionLength = 4000;

    private readonly IProhibitedItemScanner _scanner;
    private readonly IFlaggedRequestStore _flaggedStore;

    public ProhibitedItemsScanController(
        IProhibitedItemScanner scanner,
        IFlaggedRequestStore flaggedStore)
    {
        _scanner = scanner;
        _flaggedStore = flaggedStore;
    }

    [HttpPost("scan")]
    [ProducesResponseType(typeof(ScanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Scan([FromBody] ScanRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Description))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "description is required and cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Description.Length > MaxDescriptionLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"description must be {MaxDescriptionLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _scanner.ScanAsync(body.Description, ct);

        string? flaggedId = null;
        if (result.RequiresReview)
        {
            var flagged = await _flaggedStore.CreateAsync(new FlaggedRequestCreate
            {
                RequestId = string.IsNullOrWhiteSpace(body.RequestId) ? null : body.RequestId,
                UserId = userId,
                Description = body.Description,
                Matches = result.Matches
            }, ct);
            flaggedId = flagged.Id;
        }

        return Ok(new ScanResponse
        {
            Matches = result.Matches.Select(m => m.ToDto()).ToList(),
            RequiresReview = result.RequiresReview,
            FlaggedRequestId = flaggedId,
            AutoBlocked = false
        });
    }
}
