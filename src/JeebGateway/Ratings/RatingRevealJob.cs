using JeebGateway.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Ratings;

public sealed class RatingRevealOptions
{
    public const string SectionName = "RatingReveal";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan BlindPeriod { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// T-backend-021 (JEEB-39): background job that scans for ratings past the
/// 7-day blind window and reveals them to both parties.
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
                await using var scope = _scopes.CreateAsyncScope();
                var ratingStore = scope.ServiceProvider.GetService<IRatingStoreExtended>();
                if (ratingStore is null) continue;

                var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
                var cutoff = _clock.GetUtcNow() - _opts.Value.BlindPeriod;
                var unrevealed = await ratingStore.ListUnrevealedBeforeAsync(cutoff, stoppingToken);

                var revealed = 0;
                foreach (var rating in unrevealed)
                {
                    var ok = await ratingStore.TryRevealAsync(rating.Id, _clock.GetUtcNow(), stoppingToken);
                    if (!ok) continue;

                    revealed++;

                    await push.SendAsync(new PushNotificationRequest(
                        UserId: rating.RatedUserId,
                        Trigger: NotificationTrigger.RatingRevealed,
                        Title: "Rating revealed",
                        Body: $"Your delivery rating has been revealed: {rating.Score}/5",
                        Data: new Dictionary<string, string>
                        {
                            ["ratingId"] = rating.Id,
                            ["deliveryId"] = rating.DeliveryId
                        }),
                        stoppingToken);
                }

                if (revealed > 0)
                    _log.LogInformation("Rating reveal job: revealed {Count} ratings", revealed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rating reveal job failed");
            }
        }
    }
}
