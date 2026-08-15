using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Background worker that drives the data-export pipeline (T-backend-042).
/// Pulls one queued request at a time, packages it, marks it ready, and
/// fires the notifier so the user receives the secure download link.
///
/// Tests bypass the timer loop by calling <see cref="ProcessOnceAsync"/>
/// directly — the production cadence (<see cref="DataExportOptions.SweepInterval"/>)
/// is well under the 72-hour SLA, so a missed tick is recoverable.
/// </summary>
public class DataExportProcessor : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<DataExportOptions> _options;
    private readonly ILogger<DataExportProcessor> _logger;

    public DataExportProcessor(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<DataExportOptions> options,
        ILogger<DataExportProcessor> logger)
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
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data export sweep failed");
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

    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDataExportStore>();
        var packager = scope.ServiceProvider.GetRequiredService<IDataExportPackager>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDataExportNotifier>();
        var opts = _options.Value;

        var processed = 0;
        while (true)
        {
            var now = _clock.GetUtcNow();
            var claimed = await store.ClaimNextAsync(now, ct);
            if (claimed is null) return processed;

            try
            {
                var payload = await packager.BuildAsync(claimed.UserId, claimed.Format, ct);
                var readyAt = _clock.GetUtcNow();
                var token = await store.MarkReadyAsync(
                    claimed.Id,
                    payload.Bytes,
                    payload.ContentType,
                    readyAt,
                    opts.LinkValidity,
                    ct);

                await notifier.NotifyReadyAsync(claimed.UserId, claimed.Id, token, readyAt + opts.LinkValidity, ct);
                _logger.LogInformation(
                    "Data export {ExportId} ready for user {UserId} ({Bytes} bytes)",
                    claimed.Id, claimed.UserId, payload.Bytes.LongLength);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data export {ExportId} failed for user {UserId}", claimed.Id, claimed.UserId);
                await store.MarkFailedAsync(claimed.Id, ex.Message, _clock.GetUtcNow(), ct);
            }
        }
    }
}
