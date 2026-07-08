using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Ratings;

public sealed class RatingRevealOptions
{
    public const string SectionName = "RatingReveal";
    public string WindowName { get; set; } = "ratings-mutual-blind";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RatingWindow { get; set; } = BlindRevealPolicy.DefaultRatingWindow;
}

/// <summary>
/// T-backend-021 (JEEB-39): background job that scans for rating windows past the
/// named 7-day blind window. It reveals only mutually-rated pairs and closes
/// one-sided/empty windows without revealing.
///
/// JEB-1502: <see cref="SweepOnceAsync"/> is the extracted sweep body, shared
/// between the background loop and the test control-plane force-runner. The
/// control plane runs this same method so the test proves the real code path.
/// </summary>
public sealed class RatingRevealJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptions<RatingRevealOptions> _opts;
    private readonly ILogger<RatingRevealJob> _log;

    public RatingRevealJob(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptions<RatingRevealOptions> opts,
        ILogger<RatingRevealJob> log)
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
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rating reveal job failed");
            }
        }
    }

    /// <summary>
    /// Execute one sweep: reveal mutually-rated expired windows and close all
    /// other expired windows without auto-revealing one-sided ratings.
    /// Called by the background loop AND by the JEB-1502 test control-plane
    /// force-runner — no test-only logic forks.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var ratingStore = scope.ServiceProvider.GetService<IRatingStoreExtended>();
        if (ratingStore is null)
        {
            _log.LogError(
                "Rating reveal job skipped because no IRatingStoreExtended is registered; the 7-day blind rating sweep is not running.");
            return;
        }

        var now = _clock.GetUtcNow();
        var options = _opts.Value;
        var cutoff = now - options.RatingWindow;
        var result = await ratingStore.SweepExpiredWindowsAsync(cutoff, now, ct);

        if (result.RevealedCount > 0 || result.ClosedCount > 0)
        {
            _log.LogInformation(
                "Rating reveal job swept {WindowName}: revealed {RevealedCount} mutual windows and closed {ClosedCount} non-mutual windows",
                options.WindowName,
                result.RevealedCount,
                result.ClosedCount);
        }
    }
}
