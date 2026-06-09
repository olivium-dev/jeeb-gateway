using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

public sealed class WeeklySettlementOptions
{
    public const string SectionName = "WeeklySettlement";
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
    public DayOfWeek SettlementDay { get; set; } = DayOfWeek.Monday;
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// IANA timezone the weekly COD settlement cadence is evaluated in
    /// (JEB-1476). The "Beirut weekly" cadence is a Jeeb PRODUCT business rule
    /// and therefore lives HERE in the gateway, not in the shared payment
    /// gateway. <see cref="SettlementDay"/> is interpreted in this zone so the
    /// batch fires on the local Monday rather than a UTC Monday.
    /// </summary>
    public string TimeZoneId { get; set; } = "Asia/Beirut";
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

            // Evaluate the cadence in the configured product timezone (default
            // Asia/Beirut) — the Beirut-weekly COD cadence is a Jeeb business
            // rule owned by the gateway (JEB-1476).
            var now = ToConfiguredZone(_clock.GetUtcNow());
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

    /// <summary>
    /// Converts a UTC instant into the configured settlement timezone
    /// (default Asia/Beirut). Falls back to the original instant if the host
    /// does not know the timezone id, so the batch never crashes on a missing
    /// tz database entry.
    /// </summary>
    private DateTimeOffset ToConfiguredZone(DateTimeOffset utcNow)
    {
        var tzId = _opts.Value.TimeZoneId;
        if (string.IsNullOrWhiteSpace(tzId))
            return utcNow;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTime(utcNow, tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _log.LogWarning(ex, "Settlement timezone {TimeZoneId} not found; evaluating cadence in UTC", tzId);
            return utcNow;
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
