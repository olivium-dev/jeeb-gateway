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
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISettlementBatchStore>();

                var pending = await store.ListUnsettledAsync(_opts.Value.MaxBatchSize, stoppingToken);
                if (pending.Count == 0)
                {
                    _log.LogInformation("Weekly settlement batch: no unsettled items");
                    continue;
                }

                var ids = pending.Select(s => s.Id).ToList();
                await store.MarkBatchProcessedAsync(ids, now, stoppingToken);

                _log.LogInformation(
                    "Weekly settlement batch processed {Count} settlements", ids.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Weekly settlement batch failed");
            }
        }
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
