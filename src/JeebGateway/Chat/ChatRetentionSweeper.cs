using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Chat;

public sealed class ChatRetentionOptions
{
    public const string SectionName = "ChatRetention";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(90);
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// T-backend-037 (JEEB-127): background job that purges chat messages
/// older than the configured retention period. Runs every 6 hours by default.
/// </summary>
public sealed class ChatRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptions<ChatRetentionOptions> _opts;
    private readonly ILogger<ChatRetentionSweeper> _log;

    public ChatRetentionSweeper(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptions<ChatRetentionOptions> opts,
        ILogger<ChatRetentionSweeper> log)
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
            await Task.Delay(_opts.Value.SweepInterval, _clock, stoppingToken);

            try
            {
                var cutoff = _clock.GetUtcNow() - _opts.Value.RetentionPeriod;
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IChatRetentionStore>();

                var deleted = await store.PurgeBeforeAsync(cutoff, _opts.Value.BatchSize, stoppingToken);
                if (deleted > 0)
                    _log.LogInformation(
                        "Chat retention sweep: purged {Count} messages older than {Cutoff}",
                        deleted, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Chat retention sweep failed");
            }
        }
    }
}

public interface IChatRetentionStore
{
    Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct);
}

public sealed class InMemoryChatRetentionStore : IChatRetentionStore
{
    public Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
        => Task.FromResult(0);
}
