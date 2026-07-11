using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Availability;

/// <summary>
/// Background sweeper that flips Jeebers offline after
/// <see cref="AutoOfflineOptions.InactivityWindow"/> of no GPS heartbeat
/// and no in-app interaction (T-backend-023). Each transition triggers a
/// push notification so the user knows why their offer feed went quiet.
/// </summary>
public class AutoOfflineSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<AutoOfflineOptions> _options;
    private readonly ILogger<AutoOfflineSweeper> _logger;

    public AutoOfflineSweeper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<AutoOfflineOptions> options,
        ILogger<AutoOfflineSweeper> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
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
                _logger.LogError(ex, "Auto-offline sweep failed");
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
        var store = scope.ServiceProvider.GetRequiredService<IAvailabilityStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IAutoOfflineNotifier>();

        var window = _options.Value.InactivityWindow;
        var now = _clock.GetUtcNow();
        var cutoff = now - window;

        var online = await store.ListOnlineAsync(ct);
        foreach (var record in online)
        {
            // No interaction yet (just-flipped-online edge case): treat
            // LastSeenAt as the watermark, since go-online stamps both.
            var watermark = record.LastInteractionAt ?? record.LastSeenAt;
            if (watermark is null || watermark > cutoff) continue;

            try
            {
                var result = await store.GoOfflineAsync(record.UserId, GoOfflineReason.AutoOfflineInactive, ct);
                if (result.WasOnline)
                {
                    await notifier.NotifyAutoOfflineAsync(record.UserId, now, ct);
                    _logger.LogInformation(
                        "Jeeber {UserId} auto-offlined after {WindowMinutes}m of inactivity ({Withdrawn} offers withdrawn)",
                        record.UserId,
                        window.TotalMinutes,
                        result.WithdrawnOffers);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown / sweep cancellation — propagate so ExecuteAsync can
                // break the loop, never treat it as a per-record miss.
                throw;
            }
            catch (Exception ex)
            {
                // Per-record resilience (mirrors the N13 best-effort mirror in
                // AvailabilityController.TryGoOfflineMirrorAsync): one Jeeber's
                // offline write faulting — e.g. the live-upstream
                // UpstreamPendingOffersStore.WithdrawForJeeberAsync throwing
                // NotSupportedException at Offer=true — must NOT abort the whole
                // sweep. Log and skip so the remaining stale Jeebers still get
                // flipped offline this cycle.
                _logger.LogWarning(
                    ex,
                    "Auto-offline for jeeber {UserId} faulted; skipping this record and continuing the sweep.",
                    record.UserId);
            }
        }
    }
}
