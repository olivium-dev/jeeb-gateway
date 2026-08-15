using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Background activator that opens the matching window for scheduled
/// deliveries (T-backend-046, Phase 2).
///
/// A scheduled request lives in <see cref="RequestStatus.Scheduled"/>
/// from creation until <c>ScheduledAt - MatchingBuffer</c>. At that
/// moment this activator:
///
///   * atomically transitions the row to <see cref="RequestStatus.Pending"/>
///     via <see cref="IRequestsStore.TryActivateScheduledAsync"/> — once
///     pending, the existing request-expiry sweeper, offer-service, and
///     all downstream matching paths apply identically to an immediate
///     delivery (acceptance criterion: "matching triggered 30 min before
///     scheduled time");
///   * fires a one-shot Client reminder ("your scheduled delivery starts
///     matching now") via <see cref="IScheduledDeliveryNotifier"/>.
///
/// The Jeeber-side reminder is the responsibility of the matching-event
/// fan-out downstream — once the now-pending row enters <c>matched</c>,
/// notification-service pushes "you have a scheduled pickup at HH:MM" to
/// every candidate Jeeber.
/// </summary>
public class ScheduledDeliveryActivator : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<ScheduledDeliveryOptions> _options;
    private readonly ILogger<ScheduledDeliveryActivator> _logger;

    public ScheduledDeliveryActivator(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<ScheduledDeliveryOptions> options,
        ILogger<ScheduledDeliveryActivator> logger)
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
                _logger.LogError(ex, "Scheduled-delivery activator sweep failed");
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

    /// <summary>
    /// Single sweep — public so integration tests can drive it deterministically
    /// against a fake clock, exactly like <c>RequestExpirySweeper.SweepOnceAsync</c>.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IScheduledDeliveryNotifier>();
        var opts = _options.Value;

        var now = _clock.GetUtcNow();
        // Any scheduled row whose moment is at most MatchingBuffer away.
        // ScheduledAt <= now + MatchingBuffer ⇔ now >= ScheduledAt - MatchingBuffer.
        var cutoff = now + opts.MatchingBuffer;

        var due = await store.ListScheduledDueAsync(cutoff, ct);

        foreach (var req in due)
        {
            // The atomic transition guarantees the reminder fires at most
            // once per request — if a previous sweep already activated this
            // row (or the Client cancelled it before its window), the call
            // returns false and we move on.
            if (await store.TryActivateScheduledAsync(req.Id, now, ct))
            {
                await notifier.NotifyClientMatchingWindowOpenedAsync(
                    req.ClientId,
                    req.Id,
                    req.ScheduledAt!.Value,
                    now,
                    ct);
                _logger.LogInformation(
                    "Scheduled request {RequestId} activated at {Now:o} (scheduled for {ScheduledAt:o})",
                    req.Id,
                    now,
                    req.ScheduledAt);
            }
        }
    }
}
