using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Background sweeper for the no-offer nudge. The nudge is a notification
/// heuristic over the offer window, not a delivery-lifecycle fact, so it stays
/// in the gateway. It is stateless because the expiry notifier uses the existing
/// notification outbox to deduplicate <c>request-nudge:{requestId}</c>.
/// </summary>
public class RequestNudgeSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<RequestExpiryOptions> _options;
    private readonly TierExpiryWindowResolver _windows;
    private readonly ILogger<RequestNudgeSweeper> _logger;

    public RequestNudgeSweeper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<RequestExpiryOptions> options,
        TierExpiryWindowResolver windows,
        ILogger<RequestNudgeSweeper> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _windows = windows;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Request nudge sweep failed");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRequestExpiryNotifier>();
        var tiers = scope.ServiceProvider.GetRequiredService<JeebGateway.Tiers.ITiersStore>();

        var now = _clock.GetUtcNow();
        var tierTtls = await _windows.LoadTierTtlsAsync(tiers, ct);
        var scanCutoff = now - _options.Value.NoOfferNudgeWindow;
        var candidates = await store.ListPendingCreatedAtOrBeforeAsync(scanCutoff, ct);

        foreach (var req in candidates)
        {
            if (req.Status != RequestStatus.Pending) continue;
            if (req.CreatedAt > now - _options.Value.NoOfferNudgeWindow) continue;

            // Do not nudge a request that is already past its terminal tier TTL —
            // it is about to get (or already got) the harsher "expired" push and
            // a simultaneous "try expanding tier" would be confusing. This
            // preserves the exact precedence the old combined sweeper had.
            if (req.CreatedAt <= now - _windows.ResolveExpiryWindow(req, tierTtls)) continue;

            await notifier.NotifyTryExpandTierAsync(req.ClientId, req.Id, now, ct);
            _logger.LogInformation(
                "Request {RequestId} hit {WindowMinutes}m no-offer mark — sent try-expanding-tier prompt",
                req.Id,
                _options.Value.NoOfferNudgeWindow.TotalMinutes);
        }
    }
}
