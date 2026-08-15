using System.ComponentModel.DataAnnotations;
using System.Globalization;
using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin portal settlement surface (extracted from PR #364). The PR proxied
/// these routes to unified-payment-gateway; under the 2026-07-27 COD-only
/// ruling they are served by the gateway's own settlement owner instead via
/// <see cref="IAdminSettlementPortalService"/>. Wire shapes are preserved
/// verbatim. Coexists with the legacy <c>v1/admin/settlements/batches</c>
/// controller, which stays untouched.
/// </summary>
[ApiController]
public sealed class AdminCodSettlementsController : ControllerBase
{
    private static readonly TimeSpan MfaFreshness = TimeSpan.FromMinutes(5);
    private readonly IAdminSettlementPortalService _portal;
    private readonly TimeProvider _clock;

    public AdminCodSettlementsController(
        IAdminSettlementPortalService portal,
        TimeProvider clock)
    {
        _portal = portal;
        _clock = clock;
    }

    [HttpGet("admin/v1/settlements")]
    // W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
    [HttpGet("admin/settlements")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementPageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "query"), StringLength(200)] string? query,
        [FromQuery(Name = "status"), StringLength(32)] string? status,
        [FromQuery(Name = "providerId"), StringLength(128)] string? providerId,
        [FromQuery(Name = "deliveryId"), StringLength(128)] string? deliveryId,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "sort"), StringLength(32)] string? sort,
        [FromQuery(Name = "limit"), Range(1, 200)] int? limit,
        [FromQuery(Name = "cursor"), StringLength(2048)] string? cursor,
        CancellationToken ct)
    {
        PreventCaching();
        if (!TryParseBound(from, out var fromBound) || !TryParseBound(to, out var toBound))
            return Problem(
                title: "from/to must be ISO-8601 timestamps.",
                statusCode: StatusCodes.Status400BadRequest);
        var page = await _portal.ListAsync(
            new AdminSettlementPortalListRequest(
                query, status, providerId, deliveryId, fromBound, toBound,
                sort, limit ?? 50, cursor),
            ct);
        return Ok(page);
    }

    [HttpGet("admin/v1/settlements/{settlementId}")]
    [HttpGet("admin/settlements/{settlementId}")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(string settlementId, CancellationToken ct)
    {
        PreventCaching();
        if (string.IsNullOrWhiteSpace(settlementId) || settlementId.Length > 128)
            return NotFound();
        var detail = await _portal.GetAsync(settlementId.Trim(), ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpGet("admin/v1/settlement-batches/{batchId}")]
    [HttpGet("admin/settlement-batches/{batchId}")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementBatchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Batch(string batchId, CancellationToken ct)
    {
        PreventCaching();
        if (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 128)
            return NotFound();
        var batch = await _portal.GetBatchAsync(batchId.Trim(), ct);
        return batch is null ? NotFound() : Ok(batch);
    }

    [HttpPost("admin/v1/settlement-batches/{batchId}/mark-paid")]
    [HttpPost("admin/settlement-batches/{batchId}/mark-paid")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementReconcileResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReconcileBatch(
        string batchId,
        [FromBody] AdminMarkSettlementPaidRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        PreventCaching();
        if (request is null || request.ExpectedVersion < 1
                            || string.IsNullOrWhiteSpace(request.PaymentReference)
                            || request.PaymentReference.Trim().Length > 256
                            || string.IsNullOrWhiteSpace(request.Reason)
                            || request.Reason.Trim().Length > 2_000)
            return Problem(
                title: "expectedVersion and bounded paymentReference/reason values are required.",
                statusCode: StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Trim().Length is < 8 or > 200)
            return Problem(
                title: "A valid Idempotency-Key is required.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!HasFreshMfa())
            return Problem(
                type: "https://jeeb.dev/errors/mfa-required",
                title: "Fresh multi-factor authentication is required.",
                detail: "Complete administrator step-up authentication before reconciling a settlement.",
                statusCode: StatusCodes.Status403Forbidden);
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
            return unauthorized;

        var result = await _portal.MarkBatchPaidAsync(
            batchId, request.ExpectedVersion, request.PaymentReference.Trim(),
            request.Reason.Trim(), adminId, ct);
        switch (result.Outcome)
        {
            case AdminSettlementMarkPaidOutcome.NotFound:
                return Problem(
                    type: "https://jeeb.dev/errors/batch-not-found",
                    title: "Settlement batch not found.",
                    statusCode: StatusCodes.Status404NotFound);
            case AdminSettlementMarkPaidOutcome.VersionConflict:
                return Problem(
                    type: "https://jeeb.dev/errors/version-conflict",
                    title: "The settlement batch changed since it was read.",
                    statusCode: StatusCodes.Status409Conflict);
            case AdminSettlementMarkPaidOutcome.Replayed:
                Response.Headers["Idempotency-Replayed"] = "true";
                return Ok(result.Response);
            default:
                return Ok(result.Response);
        }
    }

    [HttpPost("admin/v1/settlements/{settlementId}/dispute")]
    [HttpPost("admin/settlements/{settlementId}/dispute")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Dispute(
        string settlementId,
        [FromBody] AdminDisputeSettlementRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => MutateSettlement(
            settlementId, request?.ExpectedVersion, request?.Reason, idempotencyKey);

    [HttpPost("admin/v1/settlements/{settlementId}/resolve")]
    [HttpPost("admin/settlements/{settlementId}/resolve")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Resolve(
        string settlementId,
        [FromBody] AdminResolveSettlementRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => MutateSettlement(
            settlementId, request?.ExpectedVersion, request?.ResolutionNote, idempotencyKey);

    private Task<IActionResult> MutateSettlement(
        string settlementId,
        int? expectedVersion,
        string? reason,
        string? idempotencyKey)
    {
        PreventCaching();
        if (string.IsNullOrWhiteSpace(settlementId) || settlementId.Length > 128
            || expectedVersion is null or < 1
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2_000)
            return Task.FromResult<IActionResult>(Problem(
                title: "A bounded reason and expectedVersion are required.",
                statusCode: StatusCodes.Status400BadRequest));
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Trim().Length is < 8 or > 200)
            return Task.FromResult<IActionResult>(Problem(
                title: "A valid Idempotency-Key is required.",
                statusCode: StatusCodes.Status400BadRequest));
        if (!HasFreshMfa())
            return Task.FromResult<IActionResult>(Problem(
                type: "https://jeeb.dev/errors/mfa-required",
                title: "Fresh multi-factor authentication is required.",
                statusCode: StatusCodes.Status403Forbidden));

        // The in-gateway COD owner has no dispute ledger yet; fail closed rather
        // than fabricate durable dispute state (flagged in the extraction PR).
        return Task.FromResult<IActionResult>(Problem(
            type: "https://jeeb.dev/errors/settlement-action-unsupported",
            title: "Settlement disputes are not supported by the in-gateway COD owner yet.",
            statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    private bool HasFreshMfa()
    {
        var methods = User.FindAll("amr")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!methods.Any(method => string.Equals(method, "mfa", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(method, "otp", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(method, "webauthn", StringComparison.OrdinalIgnoreCase)))
            return false;

        var rawAuthTime = User.FindFirst("auth_time")?.Value;
        if (!long.TryParse(rawAuthTime, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            return false;
        var age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix);
        return age >= TimeSpan.Zero && age <= MfaFreshness;
    }

    private static bool TryParseBound(string? raw, out DateTimeOffset? bound)
    {
        bound = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;
        bound = parsed;
        return true;
    }

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}

public sealed record AdminMarkSettlementPaidRequest(
    int ExpectedVersion,
    string? PaymentReference,
    string? Reason);

public sealed record AdminDisputeSettlementRequest(int ExpectedVersion, string? Reason);
public sealed record AdminResolveSettlementRequest(int ExpectedVersion, string? ResolutionNote);
