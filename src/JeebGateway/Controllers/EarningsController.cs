using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/earnings")]
// ADR-005 L2 §D jeeber-only: earnings are a jeeber-domain capability per the authoritative map.
// Today these reads are L1-identified (UserIdentity caller-scoped) but carried no explicit L2
// user-type gate; this declares the documented {jeeber} type (own-rows scoping stays STATE in-action).
public sealed class EarningsController : ControllerBase
{
    private readonly IEarningsAggregationService _earnings;
    private readonly IEarningsPdfGenerator _pdf;

    public EarningsController(
        IEarningsAggregationService earnings,
        IEarningsPdfGenerator pdf)
    {
        _earnings = earnings;
        _pdf = pdf;
    }

    [HttpGet("summary")]
    [RequireCapability(Capabilities.EarningsReadOwn)] // §D STATE: ownership in-action
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = to ?? DateTimeOffset.UtcNow;
        var summary = await _earnings.GetSummaryAsync(userId, start, end, ct);
        return Ok(summary);
    }

    [HttpGet("daily")]
    [RequireCapability(Capabilities.EarningsReadOwn)] // §D STATE: ownership in-action
    public async Task<IActionResult> GetDailyBreakdown(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = to ?? DateTimeOffset.UtcNow;
        var daily = await _earnings.GetDailyBreakdownAsync(userId, start, end, ct);
        return Ok(daily);
    }

    [HttpGet("lifetime")]
    [RequireCapability(Capabilities.EarningsReadOwn)] // §D STATE: ownership in-action
    public async Task<IActionResult> GetLifetime(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var summary = await _earnings.GetLifetimeSummaryAsync(userId, ct);
        return Ok(summary);
    }

    [HttpGet("statement")]
    [RequireCapability(Capabilities.EarningsPdfOwn)] // §D STATE: ownership in-action
    public async Task<IActionResult> DownloadStatement(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string language = "en",
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var result = await _pdf.GenerateAsync(
            new EarningsStatementRequest(userId, from, to, language), ct);
        return File(result.PdfBytes, result.ContentType, result.FileName);
    }
}
