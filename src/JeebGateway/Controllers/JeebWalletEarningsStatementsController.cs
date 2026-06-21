using System.Globalization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// iter5 BATCHED-FIX B13 — settlement-statements surface the installed APK
/// (<c>DioSettlementRepository</c>) calls but the gateway never mounted (404 EMPTY):
/// <list type="bullet">
///   <item><c>GET /v1/wallet/jeeb/earnings/statements</c> — the jeeber's weekly
///     settlement statements list, shape <c>{ statements: [ { id, weekLabel,
///     totalPayout, currency, status, deliveries[] } ] }</c>.</item>
///   <item><c>GET /v1/wallet/jeeb/earnings/statements/{id}/pdf</c> — the PDF for
///     one statement period, produced by the SAME real
///     <see cref="IEarningsPdfGenerator"/> the live <c>/api/earnings/statement</c>
///     route uses.</item>
/// </list>
///
/// <para><b>Real data only.</b> The list is projected from the jeeber's REAL
/// earnings entries (<see cref="IEarningsAggregationService.GetProjectionAsync"/>,
/// the same wallet-service-backed projection that powers <c>/v1/jeeb/earnings</c>),
/// one statement row per real settlement entry. NO rows are fabricated — an empty
/// projection yields an empty <c>{ statements: [] }</c>. Identity is ALWAYS the
/// bearer (own rows only). Jeeber-typed, scoped in-action.</para>
/// </summary>
[ApiController]
[Route("v1/wallet/jeeb/earnings/statements")]
public sealed class JeebWalletEarningsStatementsController : ControllerBase
{
    // Default look-back window for the statements list when no range is supplied.
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(90);

    private readonly IEarningsAggregationService _earnings;
    private readonly IEarningsPdfGenerator _pdf;
    private readonly ILogger<JeebWalletEarningsStatementsController> _log;

    public JeebWalletEarningsStatementsController(
        IEarningsAggregationService earnings,
        IEarningsPdfGenerator pdf,
        ILogger<JeebWalletEarningsStatementsController> log)
    {
        _earnings = earnings;
        _pdf = pdf;
        _log = log;
    }

    [HttpGet]
    [RequireCapability(Capabilities.EarningsReadOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListStatements(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.Subtract(DefaultWindow);
        if (start > end)
            (start, end) = (end, start);

        var projection = await _earnings.GetProjectionAsync(userId, start, end, ct);

        // One statement row PER REAL settlement entry — newest first. No synthetic rows.
        var statements = projection.Entries
            .OrderByDescending(e => e.SettledAt)
            .Select(e => new
            {
                id = e.SettlementId,
                weekLabel = WeekLabel(e.SettledAt),
                periodLabel = WeekLabel(e.SettledAt),
                totalPayout = (double)e.Net,
                currency = string.IsNullOrWhiteSpace(e.Currency) ? "USD" : e.Currency,
                // The settlement is realised when an entry exists in the projection.
                status = "paid",
                deliveries = new[]
                {
                    new
                    {
                        deliveryId = e.DeliveryId,
                        date = e.SettledAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        tier = string.Empty,
                        fare = (double)e.Gross,
                        commission = (double)e.Commission,
                        net = (double)e.Net,
                        currency = string.IsNullOrWhiteSpace(e.Currency) ? "USD" : e.Currency,
                    }
                },
            })
            .ToList();

        return Ok(new { statements });
    }

    /// <summary>
    /// GET /v1/wallet/jeeb/earnings/statements/{id}/pdf — the PDF for the statement
    /// whose period contains the settlement. The <paramref name="id"/> is the
    /// settlement id from the list; we resolve its period from the bearer's real
    /// projection and hand it to the SAME real PDF generator the live statement
    /// route uses. A statement id that does not belong to the caller's projection
    /// is a clean 404 (never a fabricated document).
    /// </summary>
    [HttpGet("{id}/pdf")]
    [RequireCapability(Capabilities.EarningsPdfOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadStatementPdf(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var end = DateTimeOffset.UtcNow;
        var start = end.Subtract(DefaultWindow);
        var projection = await _earnings.GetProjectionAsync(userId, start, end, ct);

        var entry = projection.Entries.FirstOrDefault(e =>
            string.Equals(e.SettlementId, id, StringComparison.Ordinal));
        if (entry is null)
        {
            // Not the caller's settlement (or outside the window) — 404, no fake PDF.
            return NotFound();
        }

        // The statement period is the settlement day (the list groups one entry per
        // statement). The PDF generator owns the document; we never fabricate bytes.
        var dayStart = new DateTimeOffset(entry.SettledAt.UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var result = await _pdf.GenerateAsync(
            new EarningsStatementRequest(userId, dayStart, dayEnd, "en"), ct);

        return File(result.PdfBytes, result.ContentType, result.FileName);
    }

    private static string WeekLabel(DateTimeOffset when)
    {
        var d = when.UtcDateTime;
        var cal = CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        return $"Week {week}, {d:yyyy}";
    }
}
