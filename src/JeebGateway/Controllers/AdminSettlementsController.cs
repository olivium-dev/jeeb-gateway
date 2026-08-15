using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin settlement batch management (JEB-57, TL-PIN-JEB-498 §5).
///
/// gwdbx W2-R11: payout batches moved to settlement-service, whose /batches/* surface requires the
/// ADMIN scope. The gateway deliberately holds the SERVICE scope only, so a leaked gateway token
/// cannot pay anyone. The routes are kept and fail closed with a typed 503 that names the new home;
/// they are NOT silently emptied. Operators use the settlement-service admin API.
/// </summary>
[ApiController]
[Route("v1/admin/settlements")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("admin/settlements")]
[RequireCapability(Capabilities.SettlementsManage)]
public sealed class AdminSettlementsController : ControllerBase
{
    private readonly ILogger<AdminSettlementsController> _log;

    public AdminSettlementsController(ILogger<AdminSettlementsController> log)
    {
        _log = log;
    }

    /// <summary>List settlement batches filtered by status.</summary>
    [HttpGet("batches")]
    [ProducesResponseType(typeof(IReadOnlyList<SettlementBatchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ListBatches([FromQuery] string status = "open") => BatchesMoved();

    /// <summary>Get a single settlement batch by id.</summary>
    [HttpGet("batches/{id:guid}")]
    [ProducesResponseType(typeof(SettlementBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetBatch(Guid id) => BatchesMoved();

    /// <summary>Mark a settlement batch as paid — the bank-confirmation action.</summary>
    [HttpPost("batches/{id:guid}/mark-paid")]
    [ProducesResponseType(typeof(SettlementBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult MarkPaid(Guid id)
    {
        _log.LogWarning(
            "Settlement batch mark-paid refused for {BatchId}: the gateway holds the settlement-service "
            + "SERVICE scope only; payout is an ADMIN-scope operation.", id);
        return BatchesMoved();
    }

    private IActionResult BatchesMoved() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Title = "Settlement payout batches are served by settlement-service.",
            Detail = "The gateway holds the settlement-service SERVICE scope only. Batch reads and "
                     + "mark-paid require the ADMIN scope and are not proxied.",
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = JeebGateway.Financials.SettlementAdminScopeException.ProblemType,
        });

}

/// <summary>Response DTO for settlement batch endpoints.</summary>
public sealed record SettlementBatchResponse(
    Guid Id,
    string JeeberId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalGrossUsd,
    decimal TotalCommissionUsd,
    decimal TotalNetUsd,
    int SettlementCount,
    string Currency,
    string Status,
    DateTimeOffset? PaidAt,
    string? PaidBy,
    DateTimeOffset CreatedAt);
