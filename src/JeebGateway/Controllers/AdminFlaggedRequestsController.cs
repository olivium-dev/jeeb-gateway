using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin review queue for T-backend-048. The scanner never auto-blocks; an
/// admin clears (false positive) or upholds (genuine prohibited item) each
/// flagged record here. Upstream services consume the upheld decision via a
/// follow-up wiring task once the moderation outcomes feed is in place.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("admin/prohibited-items/flagged")]
[RequireRole(Roles.Admin)]
public class AdminFlaggedRequestsController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxNoteLength = 1000;

    private readonly IFlaggedRequestStore _store;

    public AdminFlaggedRequestsController(IFlaggedRequestStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(FlaggedRequestListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
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

        FlaggedRequestStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "status must be one of: pending, cleared, upheld.",
                    Status = StatusCodes.Status400BadRequest
                });
            }
            statusFilter = parsed;
        }

        var result = await _store.ListAsync(statusFilter, page, pageSize, ct);
        return Ok(new FlaggedRequestListResponse
        {
            Items = result.Items.Select(f => f.ToDto()).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = result.Total
        });
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(FlaggedRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null) return NotFound();
        return Ok(item.ToDto());
    }

    [HttpPost("{id}/decision")]
    [ProducesResponseType(typeof(FlaggedRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decide(
        string id,
        [FromBody] FlaggedRequestDecisionRequest body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Decision))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "decision is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!TryParseStatus(body.Decision, out var status)
            || status == FlaggedRequestStatus.Pending)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "decision must be 'cleared' or 'upheld'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Note is { Length: > MaxNoteLength })
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"note must be {MaxNoteLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var updated = await _store.DecideAsync(id, status, adminId, body.Note, ct);
        if (updated is null) return NotFound();

        return Ok(updated.ToDto());
    }

    private static bool TryParseStatus(string value, out FlaggedRequestStatus status)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "pending":
                status = FlaggedRequestStatus.Pending; return true;
            case "cleared":
                status = FlaggedRequestStatus.Cleared; return true;
            case "upheld":
                status = FlaggedRequestStatus.Upheld; return true;
            default:
                status = default; return false;
        }
    }
}
