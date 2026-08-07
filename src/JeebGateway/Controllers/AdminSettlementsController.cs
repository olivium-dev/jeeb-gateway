using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Stateless browser facade over the COD owner in unified-payment-gateway.
/// Capability checks terminate here. The owner is reachable only over private
/// ingress; no browser or service authorization credential is forwarded and no
/// settlement state is cached in this process.
/// </summary>
[ApiController]
public sealed class AdminSettlementsController : ControllerBase
{
    private const string ClientName = "admin-settlements";
    private static readonly TimeSpan MfaFreshness = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _clock;

    public AdminSettlementsController(
        IHttpClientFactory clients,
        TimeProvider clock)
    {
        _clients = clients;
        _clock = clock;
    }

    [HttpGet("admin/v1/settlements")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementPageResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Index(
        [FromQuery(Name = "query"), StringLength(200)] string? query,
        [FromQuery(Name = "status"), StringLength(32)] string? status,
        [FromQuery(Name = "providerId"), StringLength(128)] string? providerId,
        [FromQuery(Name = "deliveryId"), StringLength(128)] string? deliveryId,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "sort"), StringLength(32)] string? sort,
        [FromQuery(Name = "limit"), Range(1, 200)] int? limit,
        [FromQuery(Name = "cursor"), StringLength(2048)] string? cursor,
        CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "admin/v1/settlements" + Request.QueryString, null, null, ct);

    [HttpGet("admin/v1/settlements/{settlementId}")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Detail(string settlementId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get,
            $"admin/v1/settlements/{Uri.EscapeDataString(settlementId)}", null, null, ct);

    [HttpGet("admin/v1/settlement-batches/{batchId}")]
    [RequireCapability(Capabilities.AdminSettlementsRead)]
    [ProducesResponseType(typeof(AdminSettlementBatchResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Batch(string batchId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get,
            $"admin/v1/settlement-batches/{Uri.EscapeDataString(batchId)}", null, null, ct);

    [HttpPost("admin/v1/settlement-batches/{batchId}/mark-paid")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementReconcileResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> ReconcileBatch(
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
            return Task.FromResult<IActionResult>(Problem(
                title: "expectedVersion and bounded paymentReference/reason values are required.",
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
                detail: "Complete administrator step-up authentication before reconciling a settlement.",
                statusCode: StatusCodes.Status403Forbidden));

        return ForwardAsync(HttpMethod.Post,
            $"admin/v1/settlement-batches/{Uri.EscapeDataString(batchId)}/mark-paid",
            request, idempotencyKey.Trim(), ct);
    }

    [HttpPost("admin/v1/settlements/{settlementId}/dispute")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Dispute(
        string settlementId,
        [FromBody] AdminDisputeSettlementRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => MutateSettlement(
            settlementId, "dispute", request?.ExpectedVersion, request?.Reason,
            request, idempotencyKey, ct);

    [HttpPost("admin/v1/settlements/{settlementId}/resolve")]
    [RequireCapability(Capabilities.AdminSettlementsManage)]
    [ProducesResponseType(typeof(AdminSettlementDetailResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Resolve(
        string settlementId,
        [FromBody] AdminResolveSettlementRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => MutateSettlement(
            settlementId, "resolve", request?.ExpectedVersion, request?.ResolutionNote,
            request, idempotencyKey, ct);

    private Task<IActionResult> MutateSettlement(
        string settlementId,
        string action,
        int? expectedVersion,
        string? reason,
        object? request,
        string? idempotencyKey,
        CancellationToken ct)
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

        return ForwardAsync(HttpMethod.Post,
            $"admin/v1/settlements/{Uri.EscapeDataString(settlementId)}/{action}",
            request, idempotencyKey.Trim(), ct);
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

    private async Task<IActionResult> ForwardAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        PreventCaching();
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
            return unauthorized;

        var client = _clients.CreateClient(ClientName);
        if (client.BaseAddress is null)
            return Problem(
                type: "https://jeeb.dev/errors/settlement-owner-unavailable",
                title: "The settlement owner is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        using var message = new HttpRequestMessage(method, relativeUrl);
        message.Headers.TryAddWithoutValidation("X-Admin-Id", adminId);
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", HttpContext.TraceIdentifier);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null) message.Content = JsonContent.Create(body);

        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 500)
                return Problem(
                    type: "https://jeeb.dev/errors/settlement-owner-unavailable",
                    title: "The settlement owner is temporarily unavailable.",
                    statusCode: StatusCodes.Status502BadGateway);
            if (payload.Length > 1_000_000 || !IsJson(payload))
                return Problem(
                    type: "https://jeeb.dev/errors/invalid-owner-response",
                    title: "The settlement owner returned an invalid response.",
                    statusCode: StatusCodes.Status502BadGateway);
            Response.Headers.CacheControl = "no-store";
            if (response.Headers.TryGetValues("Idempotency-Replayed", out var replayed))
                Response.Headers["Idempotency-Replayed"] = replayed.FirstOrDefault();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = payload,
                ContentType = contentType,
            };
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Problem(
                type: "https://jeeb.dev/errors/settlement-owner-unavailable",
                title: "The settlement owner is temporarily unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private static bool IsJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record AdminMarkSettlementPaidRequest(
    int ExpectedVersion,
    string? PaymentReference,
    string? Reason);

public sealed record AdminDisputeSettlementRequest(int ExpectedVersion, string? Reason);
public sealed record AdminResolveSettlementRequest(int ExpectedVersion, string? ResolutionNote);

/// <summary>Retained only so older generated consumers can deserialize during migration.</summary>
[Obsolete("Use the generic COD settlement owner contract.")]
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
