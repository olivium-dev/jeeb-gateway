using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Push;

/// <summary>
/// Background sweep that drives the 30-second retry path. Every
/// <see cref="PushOptions.RetryQueueScanInterval"/> the processor drains
/// every entry whose <see cref="PushRetryEntry.DueAt"/> is in the past and
/// hands it back to <see cref="PushNotificationService.SendForRetryAsync"/>.
/// Drained entries are NOT re-enqueued — the AC is "retried once".
/// </summary>
public sealed class PushRetryQueueProcessor : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<PushOptions> _options;
    private readonly ILogger<PushRetryQueueProcessor> _log;

    public PushRetryQueueProcessor(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<PushOptions> options,
        ILogger<PushRetryQueueProcessor> log)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.RetryQueueScanInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "push retry queue scan failed");
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

    /// <summary>Exposed so integration tests can drive a deterministic single sweep.</summary>
    public async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IPushRetryQueue>();
        var service = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        if (service is not PushNotificationService impl)
        {
            // Custom IPushNotificationService bound — caller is responsible
            // for their own retry path. Nothing to do here.
            return;
        }

        var due = await queue.DrainDueAsync(_clock.GetUtcNow(), ct);
        foreach (var entry in due)
        {
            try
            {
                await impl.SendForRetryAsync(entry.Request, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "push retry attempt threw for user {UserId} trigger {Trigger}",
                    entry.Request.UserId, entry.Request.Trigger);
            }
        }
    }
}
