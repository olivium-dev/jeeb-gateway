using JeebGateway.Financials;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/admin/finance")]
public sealed class AdminFinanceController : ControllerBase
{
    private readonly IAdminFinanceDashboardService _dashboard;

    public AdminFinanceController(IAdminFinanceDashboardService dashboard)
        => _dashboard = dashboard;

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var summary = await _dashboard.GetDashboardAsync(from, to, ct);
        return Ok(summary);
    }

    [HttpGet("top-earners")]
    public async Task<IActionResult> GetTopEarners(
        [FromQuery] int limit = 20,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var earners = await _dashboard.GetTopEarnersAsync(limit, from, to, ct);
        return Ok(earners);
    }
}
