using System.Globalization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace JeebGateway.Controllers;

/// <summary>
/// JEB-58 — Earnings Ledger + Period Aggregation (TL-PIN-JEB-510 §3).
///
/// Route: GET /v1/jeebers/me/earnings?period={week|month|year|custom}&amp;from=&amp;to=
///
/// Only <c>batched</c> and <c>paid</c> settlements are included (TL-PIN §3):
/// <c>recorded</c> rows are pending batch and excluded from earnings.
///
/// Cache key: <c>earnings:{jeeberId}:{period}:{from:yyyyMMdd}:{to:yyyyMMdd}</c>
/// TTL: 5 min (active period), 1 hour (closed period), 24 h (paid period).
/// </summary>
[ApiController]
[Route("v1/jeebers/me/earnings")]
[RequireCapability(Capabilities.EarningsReadOwn)]
public sealed class JeebEarningsController : ControllerBase
{
    /// <summary>
    /// JEBV4-283: COD states that count as EARNED commission for the jeeber. <c>recorded</c>
    /// IS included — the jeeber earns the commission the moment COD is collected at delivery
    /// completion, independent of the platform-side settlement lifecycle (<c>recorded →
    /// batched → paid</c>). Excluding it made the earnings projection structurally empty on
    /// every environment where the weekly settlement-batch loop has not yet produced a batch
    /// (on MSI settlement_batches=0), so jeebers never saw the commission they had already
    /// earned. Batching/paying is a downstream payout concern, not a precondition for the
    /// earning to be shown.
    /// </summary>
    private static readonly string[] EarningsCodStates = CodSettlementState.EarningsStates;

    private readonly IEarningsAggregationService _earnings;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public JeebEarningsController(
        IEarningsAggregationService earnings,
        IMemoryCache cache,
        TimeProvider clock)
    {
        _earnings = earnings;
        _cache    = cache;
        _clock    = clock;
    }

    /// <summary>
    /// GET /v1/jeebers/me/earnings?period={week|month|year|custom}&amp;from=&amp;to=
    ///
    /// Period semantics (TL-PIN-JEB-510 §3):
    /// - week   → Monday 00:00 UTC .. Sunday 23:59:59 UTC of current week
    /// - month  → first day of current month 00:00 UTC .. last day 23:59:59 UTC
    /// - year   → Jan 1 00:00 UTC .. Dec 31 23:59:59 UTC
    /// - custom → explicit from/to (ISO 8601); from required, to defaults to now
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(EarningsProjection), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetEarnings(
        [FromQuery] string period = "week",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauth))
            return unauth;

        var now = _clock.GetUtcNow();
        DateTimeOffset windowStart, windowEnd;

        switch (period.ToLowerInvariant())
        {
            case "week":
                windowStart = StartOfWeek(now);
                windowEnd   = windowStart.AddDays(7).AddTicks(-1);
                break;

            case "month":
                windowStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
                windowEnd   = windowStart.AddMonths(1).AddTicks(-1);
                break;

            case "year":
                windowStart = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                windowEnd   = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero);
                break;

            case "custom":
                if (from is null)
                    return BadRequest(new ProblemDetails
                    {
                        Title  = "Missing 'from' parameter for custom period.",
                        Status = StatusCodes.Status400BadRequest,
                        Type   = "https://jeeb.dev/errors/earnings-missing-from"
                    });
                windowStart = from.Value;
                windowEnd   = to ?? now;
                break;

            default:
                return BadRequest(new ProblemDetails
                {
                    Title  = $"Unknown period '{period}'. Allowed: week, month, year, custom.",
                    Status = StatusCodes.Status400BadRequest,
                    Type   = "https://jeeb.dev/errors/earnings-unknown-period"
                });
        }

        if (windowStart > windowEnd)
            return BadRequest(new ProblemDetails
            {
                Title  = "Period start must be on or before period end.",
                Detail = $"from={windowStart:O} to={windowEnd:O}",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/earnings-invalid-range"
            });

        var cacheKey = BuildCacheKey(jeeberId, period, windowStart, windowEnd);

        if (_cache.TryGetValue(cacheKey, out EarningsProjection? cached) && cached is not null)
            return Ok(cached);

        var projection = await _earnings.GetProjectionWithStatesAsync(
            jeeberId, windowStart, windowEnd, EarningsCodStates, ct);

        var ttl = ResolveTtl(windowEnd, now);
        _cache.Set(cacheKey, projection, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1,
        });

        return Ok(projection);
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset now)
    {
        var day = now.DayOfWeek;
        var daysBack = day == DayOfWeek.Sunday ? 6 : (int)day - (int)DayOfWeek.Monday;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(-daysBack);
    }

    private static string BuildCacheKey(
        string jeeberId, string period, DateTimeOffset from, DateTimeOffset to) =>
        $"earnings:{jeeberId}:{period}:{from.UtcDateTime:yyyyMMdd}:{to.UtcDateTime:yyyyMMdd}";

    private static TimeSpan ResolveTtl(DateTimeOffset windowEnd, DateTimeOffset now)
    {
        // Closed window (windowEnd < now) → 1 hour. Active window → 5 minutes.
        return windowEnd < now
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromMinutes(5);
    }
}
