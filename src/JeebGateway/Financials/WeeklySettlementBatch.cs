using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

public sealed class WeeklySettlementOptions
{
    public const string SectionName = "WeeklySettlement";
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
    public DayOfWeek SettlementDay { get; set; } = DayOfWeek.Monday;
    public int MaxBatchSize { get; set; } = 500;
}

public interface ISettlementBatchStore
{
    Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct);
    Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// T-backend-017: Weekly settlement batch.
///
/// JEB-1502: <see cref="RunBatchAsync"/> is the extracted batch body, shared
/// between the background loop and the test control-plane force-runner. It does
/// NOT enforce the <c>SettlementDay</c> check — force-runs may run on any day.
/// </summary>
public sealed class WeeklySettlementBatch : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptions<WeeklySettlementOptions> _opts;
    private readonly ILogger<WeeklySettlementBatch> _log;

    public WeeklySettlementBatch(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptions<WeeklySettlementOptions> opts,
        ILogger<WeeklySettlementBatch> log)
    {
        _scopes = scopes;
        _clock = clock;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_opts.Value.Interval, _clock, stoppingToken);

            var now = _clock.GetUtcNow();
            if (now.DayOfWeek != _opts.Value.SettlementDay)
                continue;

            try
            {
                await RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Weekly settlement batch failed");
            }
        }
    }

    /// <summary>
    /// Execute one settlement batch run. Called by the background loop AND by
    /// the JEB-1502 test control-plane force-runner (no test-only logic forks).
    /// The day-of-week check is intentionally omitted here — force-run may run
    /// any day.
    /// </summary>
    public async Task RunBatchAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettlementBatchStore>();

        var pending = await store.ListUnsettledAsync(_opts.Value.MaxBatchSize, ct);
        if (pending.Count == 0)
        {
            _log.LogInformation("Weekly settlement batch: no unsettled items");
            return;
        }

        var ids = pending.Select(s => s.Id).ToList();
        await store.MarkBatchProcessedAsync(ids, now, ct);

        _log.LogInformation(
            "Weekly settlement batch processed {Count} settlements", ids.Count);
    }
}

public sealed class InMemorySettlementBatchStore : ISettlementBatchStore
{
    private readonly ISettlementStore _inner;

    public InMemorySettlementBatchStore(ISettlementStore inner) => _inner = inner;

    public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct)
        => Task.CompletedTask;
}
