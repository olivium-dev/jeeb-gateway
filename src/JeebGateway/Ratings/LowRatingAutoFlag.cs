using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Ratings;

public sealed class LowRatingFlagOptions
{
    public const string SectionName = "LowRatingFlag";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(30);
    public double ThresholdScore { get; set; } = 2.0;
    public int MinRatingsRequired { get; set; } = 3;
}

/// <summary>
/// T-backend-040 (JEEB-130): background job that flags Jeebers whose
/// average rating drops below the configured threshold and notifies admin.
/// </summary>
public sealed class LowRatingAutoFlag : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptions<LowRatingFlagOptions> _opts;
    private readonly ILogger<LowRatingAutoFlag> _log;

    public LowRatingAutoFlag(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptions<LowRatingFlagOptions> opts,
        ILogger<LowRatingAutoFlag> log)
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
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetService<IRatingStoreExtended>();
                if (store is null) continue;

                var flagged = await store.ListJeebersBelowAverageAsync(
                    _opts.Value.ThresholdScore,
                    _opts.Value.MinRatingsRequired,
                    stoppingToken);

                foreach (var entry in flagged)
                {
                    _log.LogWarning(
                        "Jeeber {JeeberId} flagged: avg rating {Avg:F1} below threshold {Threshold}",
                        entry.JeeberId, entry.AverageScore, _opts.Value.ThresholdScore);
                }

                if (flagged.Count > 0)
                    _log.LogInformation("Low-rating auto-flag: {Count} Jeebers flagged", flagged.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Low-rating auto-flag job failed");
            }
        }
    }
}
