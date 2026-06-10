using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

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
    [ProducesResponseType(typeof(EarningsProjection), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = to ?? DateTimeOffset.UtcNow;

        // N15: a reversed closed range is a client error, not an empty period
        // (BR-17 — period vocab + tz/week-start translation is gateway-only;
        // wallet only ever sees a valid absolute from<=to).
        if (start > end)
            return InvalidRange(start, end);

        var projection = await _earnings.GetProjectionAsync(userId, start, end, ct);
        return ConditionalProjection(projection);
    }

    [HttpGet("daily")]
    [RequireCapability(Capabilities.EarningsReadOwn)] // §D STATE: ownership in-action
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDailyBreakdown(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = to ?? DateTimeOffset.UtcNow;
        if (start > end)
            return InvalidRange(start, end);

        var daily = await _earnings.GetDailyBreakdownAsync(userId, start, end, ct);
        return Ok(daily);
    }

    [HttpGet("lifetime")]
    [RequireCapability(Capabilities.EarningsReadOwn)] // §D STATE: ownership in-action
    [ProducesResponseType(typeof(EarningsProjection), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> GetLifetime(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var projection = await _earnings.GetLifetimeProjectionAsync(userId, ct);
        return ConditionalProjection(projection);
    }

    [HttpGet("statement")]
    [RequireCapability(Capabilities.EarningsPdfOwn)] // §D STATE: ownership in-action
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DownloadStatement(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string language = "en",
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        if (from > to)
            return InvalidRange(from, to);

        var result = await _pdf.GenerateAsync(
            new EarningsStatementRequest(userId, from, to, language), ct);
        return File(result.PdfBytes, result.ContentType, result.FileName);
    }

    /// <summary>
    /// Emits the projection with a strong ETag derived from its content, and
    /// short-circuits to 304 when the caller's <c>If-None-Match</c> already
    /// matches (A5 / BR-18 — a repeat conditional read does not re-transfer the
    /// body). The ETag is deterministic over the totals + entry ids so the same
    /// period yields the same tag across reads.
    /// </summary>
    private IActionResult ConditionalProjection(EarningsProjection projection)
    {
        var etag = ComputeETag(projection);
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "private, max-age=0, must-revalidate";

        var inm = Request.Headers[HeaderNames.IfNoneMatch];
        if (inm.Count > 0 && inm.Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(projection);
    }

    private static string ComputeETag(EarningsProjection p)
    {
        var sb = new StringBuilder()
            .Append(p.JeeberId).Append('|')
            .Append(p.Totals.Gross.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(p.Totals.Commission.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(p.Totals.Net.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(p.Totals.Currency).Append('|')
            .Append(p.DeliveryCount).Append('|')
            .Append(p.PeriodStart.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(p.PeriodEnd.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        foreach (var e in p.Entries)
            sb.Append('|').Append(e.SettlementId).Append(':').Append(e.Net.ToString(CultureInfo.InvariantCulture));

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))
            .ToLowerInvariant();
        return $"\"{hash}\"";
    }

    private IActionResult InvalidRange(DateTimeOffset from, DateTimeOffset to) => BadRequest(new ProblemDetails
    {
        Title = "Invalid earnings period.",
        Detail = $"'from' ({from:O}) must be on or before 'to' ({to:O}).",
        Status = StatusCodes.Status400BadRequest,
        Type = "https://jeeb.dev/errors/earnings-invalid-range",
    });
}
