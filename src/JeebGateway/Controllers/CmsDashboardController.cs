using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Kyc;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// CMS admin dashboard summary — the route the DEPLOYED back-office shell calls.
///
/// <para><b>Why this controller exists.</b> The shipped shell issues
/// <c>GET &lt;origin&gt;/gateway/cms-admin/v1/dashboard/summary</c> (byte-verified in the live
/// release bundle); the vhost strips one <c>/gateway/</c>, so the gateway must serve
/// <c>cms-admin/v1/dashboard/summary</c>. It served nothing there and the whole dashboard
/// rendered its full-page error. Same compat pattern as
/// <see cref="CmsKycAdminController"/>: serve the contract the deployed bundle already emits,
/// no CMS redeploy required.</para>
///
/// <para><b>Data honesty.</b> Every number comes from a store the gateway ALREADY owns — its own
/// <c>delivery_requests</c> mirror, its own users projection, its own settlement rows, and the
/// existing KYC seam. No new downstream dependency, no new table, no writes. These are therefore
/// the GATEWAY's projections, not delivery-service's ledger: rows the gateway never mirrored
/// (non-UUID ids, or a run with no GatewayPostgres) are not counted.</para>
///
/// <para><b>Fail-soft per widget.</b> Each KPI is computed in its own try/catch. One degraded
/// source zeroes ONE tile and logs; the endpoint does not 500 and the other seven tiles still
/// render. A dashboard that shows a zero is recoverable; one that shows an error page is not.</para>
/// </summary>
[ApiController]
[Route("cms-admin/v1/dashboard")]
// Reuses the existing finance.read capability (AdminOnly). No capability is minted here and the
// capability→role map is untouched.
[RequireCapability(Capabilities.FinanceRead)]
public sealed class CmsDashboardController : ControllerBase
{
    /// <summary>Recent-activity rows the shell's table renders.</summary>
    private const int RecentActivityLimit = 8;

    private readonly IRequestsStore _requests;
    private readonly IUsersStore _users;
    private readonly ISettlementServiceClient _settlements;
    private readonly IKycBffSeam _kyc;
    private readonly ILogger<CmsDashboardController> _log;

    public CmsDashboardController(
        IRequestsStore requests,
        IUsersStore users,
        ISettlementServiceClient settlements,
        IKycBffSeam kyc,
        ILogger<CmsDashboardController> log)
    {
        _requests = requests;
        _users = users;
        _settlements = settlements;
        _kyc = kyc;
        _log = log;
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(CmsDashboardSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var orders = await OrdersAsync(ct);
        var users = await UsersAsync(ct);

        return Ok(new CmsDashboardSummaryResponse
        {
            Kpis = new CmsDashboardKpis
            {
                OrdersTotal = orders.Total,
                OrdersInTransit = orders.InTransit,
                OrdersNeedingEscalation = orders.NeedingEscalation,
                UsersTotal = users.Total,
                JeebersTotal = users.For(Roles.Jeeber),
                ClientsTotal = users.For(Roles.Client),
                EarningsTotal = new CmsMoney
                {
                    Value = await EarningsAsync(ct),
                    Currency = SettlementService.CurrencyUsd
                },
                KycPending = await KycPendingAsync(ct)
            },
            RecentActivity = orders.Recent
        });
    }

    private async Task<OrdersWidget> OrdersAsync(CancellationToken ct)
    {
        RequestsAdminSnapshot snapshot;
        try
        {
            snapshot = await _requests.GetAdminDashboardSnapshotAsync(RecentActivityLimit, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "cms dashboard: orders snapshot unavailable; orders tiles degraded to zero");
            return new OrdersWidget(0, 0, 0, Array.Empty<CmsRecentActivityItem>());
        }

        var inTransit = 0;
        var escalation = 0;
        foreach (var (rawStatus, count) in snapshot.CountsByStatus)
        {
            switch (ToCmsOrderStatus(rawStatus))
            {
                // Mirrors the CMS reference semantics: "in transit" spans InTransit + AtDoor.
                case CanonicalDeliveryStatus.InTransit:
                case CanonicalDeliveryStatus.AtDoor:
                    inTransit += count;
                    break;
                case CanonicalDeliveryStatus.FailedNeedsEscalation:
                    escalation += count;
                    break;
            }
        }

        var recent = new List<CmsRecentActivityItem>(snapshot.Recent.Count);
        foreach (var row in snapshot.Recent)
        {
            recent.Add(new CmsRecentActivityItem
            {
                Id = row.Id,
                Title = row.Title,
                Status = ToCmsOrderStatus(row.Status) ?? CanonicalDeliveryStatus.Ordered,
                ClientName = await DisplayNameAsync(row.ClientId, ct),
                JeeberName = await DisplayNameAsync(row.JeeberId, ct),
                UpdatedAt = row.UpdatedAt
            });
        }

        return new OrdersWidget(snapshot.Total, inTransit, escalation, recent);
    }

    private async Task<UserRoleCounts> UsersAsync(CancellationToken ct)
    {
        try
        {
            // UM persists OPAQUE roles (customer/driver); translate before counting (N14).
            return await _users.CountByRolesAsync(new[] { Roles.Jeeber, Roles.Client }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "cms dashboard: user counts unavailable; user tiles degraded to zero");
            return UserRoleCounts.Empty;
        }
    }

    /// <summary>
    /// Platform-wide jeeber NET earnings, using the IDENTICAL row filter and per-row arithmetic
    /// as the per-jeeber earnings reads (cod_state ∈ EarningsStates; net = goods_cost − commission),
    /// so the dashboard and the jeeber's own earnings screen can never disagree.
    /// </summary>
    private async Task<decimal> EarningsAsync(CancellationToken ct)
    {
        try
        {
            return await _settlements.SumNetEarningsAsync(
                holderId: null, CodSettlementState.EarningsStates, from: null, to: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "cms dashboard: earnings sum unavailable; earnings tile degraded to zero");
            return 0m;
        }
    }

    private async Task<int> KycPendingAsync(CancellationToken ct)
    {
        try
        {
            return (await _kyc.GetPendingQueueAsync(1, 1, ct)).Total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // KycUpstreamDisabledException lands here too: no KYC upstream ⇒ nothing pending.
            _log.LogWarning(ex, "cms dashboard: KYC queue unavailable; kycPending degraded to zero");
            return 0;
        }
    }

    private async Task<string?> DisplayNameAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        try
        {
            var profile = await _users.GetByIdAsync(userId, ct);
            return string.IsNullOrWhiteSpace(profile?.Name) ? null : profile!.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "cms dashboard: name lookup failed for {UserId}; row listed undecorated", userId);
            return null;
        }
    }

    /// <summary>
    /// Folds a raw gateway status token onto the CMS <c>OrderStatus</c> enum. The alias layer
    /// (<see cref="DeliveryStatusAlias.ToCanonical"/>) owns the legacy→canonical table; this adds
    /// the two folds the CMS enum needs: the request-lifecycle tokens with no delivery row yet
    /// read as Ordered, <c>Expired</c> collapses onto Cancelled (the CMS enum has no Expired), and
    /// <c>cancellation_requested</c> is a row an admin must act on ⇒ FailedNeedsEscalation.
    /// Returns null for a token the gateway cannot resolve at all (never guessed).
    /// </summary>
    internal static string? ToCmsOrderStatus(string? raw)
    {
        var canonical = DeliveryStatusAlias.ToCanonical(raw);
        if (canonical is not null)
        {
            return canonical == CanonicalDeliveryStatus.Expired
                ? CanonicalDeliveryStatus.Cancelled
                : canonical;
        }

        return raw switch
        {
            RequestStatus.Scheduled or RequestStatus.Pending or RequestStatus.Matched
                => CanonicalDeliveryStatus.Ordered,
            RequestStatus.CancellationRequested
                => CanonicalDeliveryStatus.FailedNeedsEscalation,
            _ => null
        };
    }

    private sealed record OrdersWidget(
        int Total, int InTransit, int NeedingEscalation, IReadOnlyList<CmsRecentActivityItem> Recent);
}

/// <summary>CMS <c>Money</c> — major-unit decimal + currency.</summary>
public sealed class CmsMoney
{
    public required decimal Value { get; init; }
    public required string Currency { get; init; }
}

/// <summary>CMS <c>DashboardKpis</c>.</summary>
public sealed class CmsDashboardKpis
{
    public required int OrdersTotal { get; init; }
    public required int OrdersInTransit { get; init; }
    public required int OrdersNeedingEscalation { get; init; }
    public required int UsersTotal { get; init; }
    public required int JeebersTotal { get; init; }
    public required int ClientsTotal { get; init; }
    public required CmsMoney EarningsTotal { get; init; }
    public required int KycPending { get; init; }
}

/// <summary>CMS <c>RecentActivityItem</c>. <c>status</c> is the PascalCase OrderStatus token.</summary>
public sealed class CmsRecentActivityItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public string? ClientName { get; init; }
    public string? JeeberName { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>CMS <c>DashboardSummary</c>.</summary>
public sealed class CmsDashboardSummaryResponse
{
    public required CmsDashboardKpis Kpis { get; init; }
    public required IReadOnlyList<CmsRecentActivityItem> RecentActivity { get; init; }
}
