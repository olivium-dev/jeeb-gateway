using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin settlement batch management (JEB-57, TL-PIN-JEB-498 §5).
///
/// Endpoints:
/// - GET  /v1/admin/settlements/batches               — list batches by status
/// - GET  /v1/admin/settlements/batches/{id}           — get single batch
/// - POST /v1/admin/settlements/batches/{id}/mark-paid — mark batch paid (idempotent)
/// </summary>
[ApiController]
[Route("v1/admin/settlements")]
[RequireCapability(Capabilities.SettlementsManage)]
public sealed class AdminSettlementsController : ControllerBase
{
    private readonly ISettlementBatchStore _batches;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminSettlementsController> _log;

    public AdminSettlementsController(
        ISettlementBatchStore batches,
        TimeProvider clock,
        ILogger<AdminSettlementsController> log)
    {
        _batches = batches;
        _clock   = clock;
        _log     = log;
    }

    /// <summary>
    /// List settlement batches filtered by status.
    /// </summary>
    [HttpGet("batches")]
    [ProducesResponseType(typeof(IReadOnlyList<SettlementBatchResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBatches(
        [FromQuery] string status = "open",
        CancellationToken ct = default)
    {
        var batches = await _batches.ListByStatusAsync(status, ct);
        return Ok(batches.Select(MapBatch));
    }

    /// <summary>
    /// Get a single settlement batch by id.
    /// </summary>
    [HttpGet("batches/{id:guid}")]
    [ProducesResponseType(typeof(SettlementBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatch(Guid id, CancellationToken ct)
    {
        var batch = await _batches.GetByIdAsync(id, ct);
        if (batch is null) return NotFound();
        return Ok(MapBatch(batch));
    }

    /// <summary>
    /// Mark a settlement batch as paid (admin RBAC, idempotent, audit-logged via paid_by column).
    ///
    /// POST /v1/admin/settlements/batches/{id}/mark-paid
    ///
    /// On success: returns the updated batch with status=paid, paid_at, paid_by.
    /// On already-paid: idempotent — returns the existing paid batch (200, no error).
    /// </summary>
    [HttpPost("batches/{id:guid}/mark-paid")]
    [ProducesResponseType(typeof(SettlementBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkPaid(Guid id, CancellationToken ct)
    {
        var existing = await _batches.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return NotFound(new ProblemDetails
            {
                Title  = "Settlement batch not found.",
                Status = StatusCodes.Status404NotFound,
                Type   = "https://jeeb.dev/errors/batch-not-found"
            });
        }

        if (!UserIdentity.TryGetUserId(HttpContext, out var adminUserId, out var unauthorized))
            return unauthorized;

        var paidAt = _clock.GetUtcNow();
        var paid = await _batches.MarkPaidAsync(id, adminUserId, paidAt, ct);

        _log.LogInformation(
            "Settlement batch {BatchId} marked paid by admin {AdminUserId} at {PaidAt}",
            id, adminUserId, paidAt);

        return Ok(MapBatch(paid));
    }

    private static SettlementBatchResponse MapBatch(SettlementBatch b) => new(
        Id:                 b.Id,
        JeeberId:           b.JeeberId,
        PeriodStart:        b.PeriodStart,
        PeriodEnd:          b.PeriodEnd,
        TotalGrossUsd:      b.TotalGrossUsd,
        TotalCommissionUsd: b.TotalCommissionUsd,
        TotalNetUsd:        b.TotalNetUsd,
        SettlementCount:    b.SettlementCount,
        Currency:           b.Currency,
        Status:             b.Status,
        PaidAt:             b.PaidAt,
        PaidBy:             b.PaidBy,
        CreatedAt:          b.CreatedAt);
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
