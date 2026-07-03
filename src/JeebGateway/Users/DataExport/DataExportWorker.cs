using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// SLA-enforcement sweeper for the durable data-export pipeline (T-backend-042,
/// GDPR-like right of access). Complements <see cref="DataExportProcessor"/> — which
/// claims and fulfils queued exports — with a watchdog that guarantees the 72-hour
/// SLA (<see cref="DataExportOptions.Sla"/>) is never silently missed: any row still
/// <c>queued</c> or <c>processing</c> once its <see cref="DataExportRequest.DueBy"/>
/// deadline has passed is moved to <c>failed</c> so an operator can see the breach
/// and the user can open a fresh request, instead of the row sitting open forever
/// (e.g. the packager wedges mid-run, or a processor replica dies between claim and
/// completion).
///
/// Only registered when <c>GatewayPostgres:ConnectionString</c> is configured (see
/// Program.cs) — it depends on the concrete <see cref="PostgresDataExportStore"/> for
/// <see cref="PostgresDataExportStore.ListOverdueOpenAsync"/>, a capability with no
/// in-memory equivalent. <see cref="InMemoryDataExportStore"/> never shipped
/// SLA-breach detection either, so this worker is additive durability hardening, not
/// a behavioural change to the in-memory fallback path.
///
/// Same sweep skeleton as <see cref="JeebGateway.Requests.RequestExpirySweeper"/> /
/// <see cref="DataExportProcessor"/>: an outer timer loop delegates each tick to
/// <see cref="SweepOnceAsync"/>, which tests call directly to bypass the timer.
/// </summary>
public sealed class DataExportWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<DataExportOptions> _options;
    private readonly ILogger<DataExportWorker> _logger;

    public DataExportWorker(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<DataExportOptions> options,
        ILogger<DataExportWorker> logger)
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
                _logger.LogError(ex, "Data export SLA sweep failed");
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
    /// Marks every open (queued/processing) export whose SLA deadline has passed as
    /// failed. Returns the number of rows swept so tests can assert without racing
    /// the timer loop.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresDataExportStore>();

        var now = _clock.GetUtcNow();
        var overdue = await store.ListOverdueOpenAsync(now, ct);

        var count = 0;
        foreach (var row in overdue)
        {
            await store.MarkFailedAsync(
                row.Id,
                $"SLA exceeded: not fulfilled within {_options.Value.Sla.TotalHours:0}h",
                now,
                ct);
            _logger.LogWarning(
                "Data export {ExportId} for user {UserId} exceeded its SLA (dueBy={DueBy}); marked failed",
                row.Id, row.UserId, row.DueBy);
            count++;
        }
        return count;
    }
}
