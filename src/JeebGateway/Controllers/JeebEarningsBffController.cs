using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// JEEBER-SPINE Defect 3 — the Jeeb-mapped EARNINGS BFF surface the mobile app consumes
/// (<c>DioEarningsRepository</c>):
///
/// <list type="bullet">
///   <item><c>GET /v1/jeeb/earnings?jeeberId=&amp;period={today|week|month}</c> — the
///     caller's earnings summary.</item>
///   <item><c>GET /v1/jeeb/earnings/export?jeeberId=&amp;format=pdf&amp;period=</c> — the
///     earnings statement PDF download.</item>
/// </list>
///
/// <para>
/// The mobile app calls the gateway-relative <c>/v1/jeeb/earnings</c>, but the gateway only
/// exposed <c>/v1/jeebers/me/earnings</c> (<see cref="JeebEarningsController"/>) and
/// <c>/api/earnings/*</c> (<c>EarningsController</c>) — so the app's path 404'd. This
/// controller adds the missing mobile-contract path, wired to the OWNING surfaces:
/// the SUMMARY relays wallet-service's <c>GET /v1/wallet/jeeb/earnings</c> (the jeeberearnings
/// read, bound to <c>WalletServiceApi:BaseUrl</c>); the EXPORT reuses the gateway's existing
/// <see cref="IEarningsPdfGenerator"/> (the same generator <c>EarningsController</c> serves),
/// since wallet-service exposes no PDF statement endpoint.
/// </para>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): authenticates, scopes the read to the caller's OWN jeeber
/// id from the bearer (a client-supplied <c>jeeberId</c> query is NOT trusted), and relays the
/// upstream response (summary) / generates the statement (export). It holds NO state, NO
/// persistence and NO domain rules.
/// </para>
/// </summary>
[ApiController]
[Route("v1/jeeb/earnings")]
[RequireCapability(Capabilities.EarningsReadOwn)]
public sealed class JeebEarningsBffController : ControllerBase
{
    /// <summary>Named HttpClient bound to WalletServiceApi:BaseUrl (registered in Program.cs).</summary>
    public const string WalletHttpClientName = "JeebEarningsWalletClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEarningsPdfGenerator _pdf;
    private readonly TimeProvider _clock;
    private readonly ILogger<JeebEarningsBffController> _log;

    public JeebEarningsBffController(
        IHttpClientFactory httpClientFactory,
        IEarningsPdfGenerator pdf,
        TimeProvider clock,
        ILogger<JeebEarningsBffController> log)
    {
        _httpClientFactory = httpClientFactory;
        _pdf = pdf;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// GET /v1/jeeb/earnings?jeeberId=&amp;period={today|week|month} — the caller's earnings
    /// summary, relayed VERBATIM from wallet-service's <c>/v1/wallet/jeeb/earnings</c>
    /// (status + body), scoped to the bearer's own jeeber id.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetEarnings(
        [FromQuery] string? period,
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized))
            return unauthorized;

        // Own-scoping (ADR-0001): always read the CALLER's own earnings, never a query id.
        var query = $"?jeeberId={Uri.EscapeDataString(jeeberId)}";
        if (!string.IsNullOrWhiteSpace(period)) query += $"&period={Uri.EscapeDataString(period)}";
        if (!string.IsNullOrWhiteSpace(from)) query += $"&from={Uri.EscapeDataString(from)}";
        if (!string.IsNullOrWhiteSpace(to)) query += $"&to={Uri.EscapeDataString(to)}";

        var client = _httpClientFactory.CreateClient(WalletHttpClientName);
        if (client.BaseAddress is null)
        {
            _log.LogWarning("v1/jeeb/earnings: wallet upstream base address not configured (WalletServiceApi:BaseUrl).");
            return Problem(
                title: "Earnings upstream not configured.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/earnings-upstream-unconfigured");
        }

        HttpResponseMessage upstream;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1/wallet/jeeb/earnings" + query);
            var auth = Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);

            upstream = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "v1/jeeb/earnings: wallet upstream call failed.");
            return Problem(
                title: "Earnings upstream call failed.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/earnings-upstream-fault");
        }

        using (upstream)
        {
            var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";

            // Relay the upstream STATUS + body verbatim (thin BFF; the wallet owns the shape).
            // The mobile DioEarningsRepository distinguishes network-vs-server by status, so
            // the real upstream status must survive — not be flattened to 200.
            return new ContentResult
            {
                Content = System.Text.Encoding.UTF8.GetString(bytes),
                ContentType = contentType,
                StatusCode = (int)upstream.StatusCode,
            };
        }
    }

    /// <summary>
    /// GET /v1/jeeb/earnings/export?jeeberId=&amp;format=pdf&amp;period= — the earnings
    /// statement PDF, produced by the gateway's existing <see cref="IEarningsPdfGenerator"/>
    /// (wallet-service has no PDF statement endpoint). Scoped to the bearer's own jeeber id.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExportEarnings(
        [FromQuery] string? period,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string language = "en",
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized))
            return unauthorized;

        var (windowStart, windowEnd) = ResolveWindow(period, from, to);

        var result = await _pdf.GenerateAsync(
            new EarningsStatementRequest(jeeberId, windowStart, windowEnd, language), ct);

        return File(result.PdfBytes, result.ContentType, result.FileName);
    }

    /// <summary>
    /// Map the mobile period vocabulary ({today|week|month}) — or an explicit custom from/to —
    /// to a UTC window. Mirrors <see cref="JeebEarningsController"/>'s period semantics so the
    /// export and the summary cover the same period.
    /// </summary>
    private (DateTimeOffset start, DateTimeOffset end) ResolveWindow(string? period, string? from, string? to)
    {
        var now = _clock.GetUtcNow();

        if (DateTimeOffset.TryParse(from, out var parsedFrom))
        {
            var end = DateTimeOffset.TryParse(to, out var parsedTo) ? parsedTo : now;
            return (parsedFrom, end);
        }

        switch ((period ?? "week").ToLowerInvariant())
        {
            case "today":
            {
                var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
                return (start, start.AddDays(1).AddTicks(-1));
            }
            case "month":
            {
                var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
                return (start, start.AddMonths(1).AddTicks(-1));
            }
            case "week":
            default:
            {
                var day = now.DayOfWeek;
                var daysBack = day == DayOfWeek.Sunday ? 6 : (int)day - (int)DayOfWeek.Monday;
                var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-daysBack);
                return (start, start.AddDays(7).AddTicks(-1));
            }
        }
    }
}
