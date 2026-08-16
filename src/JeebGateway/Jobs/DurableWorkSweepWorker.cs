using Microsoft.Extensions.Options;

namespace JeebGateway.Jobs;

public sealed class DurableWorkSweepOptions
{
    public const string SectionName = "DurableWorkSweep";

    public bool Enabled { get; init; } = true;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Kinds swept in order on every tick. Each is claimed independently.</summary>
    public IReadOnlyList<string> Kinds { get; init; } =
    [
        DurableWorkContract.AccountDeletionKind,
        DurableWorkContract.DataExportKind,
    ];
}

/// <summary>
/// Drives <see cref="DurableWorkSweepExecutor"/> in-process. Without a driver the GDPR erasure
/// deadline and the export SLA are stored durably but nothing ever claims them when they fall due.
/// </summary>
public sealed class DurableWorkSweepWorker(
    IServiceProvider services,
    IOptions<DurableWorkSweepOptions> options,
    TimeProvider clock,
    ILogger<DurableWorkSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        if (!configured.Enabled || configured.Kinds.Count == 0)
        {
            logger.LogWarning(
                "Durable work sweep is DISABLED; GDPR purge deadlines and export SLAs will not be executed.");
            return;
        }

        // A non-positive interval would hot-spin (0) or throw out of ExecuteAsync (negative),
        // which silently kills the sweep — the exact failure mode this worker exists to prevent.
        var interval = configured.Interval > TimeSpan.Zero
            ? configured.Interval
            : TimeSpan.FromMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var kind in configured.Kinds)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await SweepKindAsync(kind, stoppingToken);
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SweepKindAsync(string kind, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<DurableWorkSweepExecutor>();
            var summary = await executor.SweepAsync(kind, requestedLimit: null, ct);
            if (summary.Claimed > 0)
            {
                logger.LogInformation(
                    "Durable sweep {Kind}: claimed {Claimed}, completed {Completed}, deferred {Deferred}, "
                    + "retried {Retried}, failed {Failed}, leaseLost {LeaseLost}, errors {Errors}",
                    kind, summary.Claimed, summary.Completed, summary.Deferred,
                    summary.Retried, summary.Failed, summary.LeaseLost, summary.Errors);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad kind (or a state-service blip) must not end the loop for the other kind.
            logger.LogError(ex, "Durable work sweep for kind {Kind} failed", kind);
        }
    }
}
