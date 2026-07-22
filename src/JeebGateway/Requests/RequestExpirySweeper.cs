using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Background sweeper that handles only terminal tier-TTL expiry. The no-offer
/// nudge lives in <see cref="RequestNudgeSweeper"/>.
///
/// IMPORTANT: This class is the LEGACY gateway-owned TTL authority. It is kept
/// only while <c>FeatureFlags:RequestExpiry:Source == "gateway"</c> and is deleted
/// in the cleanup PR once delivery-service has soaked. Never run two TTL
/// authorities at once.
/// </summary>
public class RequestExpirySweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<RequestExpiryOptions> _options;
    private readonly TierExpiryWindowResolver _windows;
    private readonly IOptionsMonitor<RequestExpirySourceOptions> _source;
    private readonly ILogger<RequestExpirySweeper> _logger;

    public RequestExpirySweeper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<RequestExpiryOptions> options,
        TierExpiryWindowResolver windows,
        IOptionsMonitor<RequestExpirySourceOptions> source,
        ILogger<RequestExpirySweeper> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _windows = windows;
        _source = source;
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
                _logger.LogError(ex, "Request expiry sweep failed");
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
        if (!_source.CurrentValue.GatewaySweeperEnabled) return;

        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRequestExpiryNotifier>();
        var offers = scope.ServiceProvider.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        var tiers = scope.ServiceProvider.GetRequiredService<JeebGateway.Tiers.ITiersStore>();

        var now = _clock.GetUtcNow();
        var tierTtls = await _windows.LoadTierTtlsAsync(tiers, ct);
        var shortestExpiryWindow = tierTtls.Count > 0
            ? tierTtls.Values.Min()
            : TierExpiryWindowResolver.SafeExpiryWindow;
        var scanCutoff = now - shortestExpiryWindow;

        var candidates = await store.ListPendingCreatedAtOrBeforeAsync(scanCutoff, ct);

        foreach (var req in candidates)
        {
            var expiryWindow = _windows.ResolveExpiryWindow(req, tierTtls);
            var expiryCutoff = now - expiryWindow;

            if (req.CreatedAt <= expiryCutoff)
            {
                if (await store.TryExpireAsync(req.Id, now, ct))
                {
                    await notifier.NotifyExpiredAsync(req.ClientId, req.Id, now, ct);

                    // Best-effort: close any still-live bids on the now-terminal request
                    // so jeebers don't hold a stale "pending" offer and a late accept
                    // can't race a dead request. The request is already durably expired;
                    // a hiccup here must never undo that, so it is swallowed.
                    try
                    {
                        var closed = await offers.ExpireForRequestAsync(req.Id, now, ct);
                        if (closed > 0)
                        {
                            _logger.LogInformation(
                                "Request {RequestId} expiry closed {ClosedCount} live offer(s)",
                                req.Id, closed);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Request {RequestId} expired but closing its live offers failed; "
                            + "offers may linger as pending until next reconcile.", req.Id);
                    }

                    _logger.LogInformation(
                        "Request {RequestId} expired after {WindowMinutes}m without accepted offer",
                        req.Id,
                        expiryWindow.TotalMinutes);
                }
            }
        }
    }
}
