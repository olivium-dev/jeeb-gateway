using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Background sweeper that fires the two delivery-request expiry events
/// (T-backend-028):
///
///   * <see cref="RequestExpiryOptions.NoOfferNudgeWindow"/> after creation
///     with the request still in <c>pending</c> — sends the "Try expanding
///     tier" prompt once.
///   * <see cref="RequestExpiryOptions.ExpiryWindow"/> after creation
///     without an accepted offer — moves the request to <c>expired</c>
///     and notifies the Client. Once expired the request is terminal and
///     cannot receive new offers (enforced inside <see cref="IRequestsStore"/>).
/// </summary>
public class RequestExpirySweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<RequestExpiryOptions> _options;
    private readonly ILogger<RequestExpirySweeper> _logger;

    public RequestExpirySweeper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<RequestExpiryOptions> options,
        ILogger<RequestExpirySweeper> logger)
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
                _logger.LogError(ex, "Request expiry sweep failed");
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

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRequestExpiryNotifier>();
        var opts = _options.Value;

        var now = _clock.GetUtcNow();
        // Pull every still-pre-acceptance request created at or before the
        // 10-min cutoff. The 30-min set is a strict subset of this set, so
        // a single scan covers both windows.
        var nudgeCutoff = now - opts.NoOfferNudgeWindow;
        var expiryCutoff = now - opts.ExpiryWindow;

        var candidates = await store.ListPendingCreatedAtOrBeforeAsync(nudgeCutoff, ct);

        foreach (var req in candidates)
        {
            // 30-min expiry takes precedence: if the request is past the
            // hard window we move it to terminal and notify, then skip the
            // nudge — the Client just got the harsher "expired" push and a
            // simultaneous "try expanding tier" would be confusing.
            if (req.CreatedAt <= expiryCutoff)
            {
                if (await store.TryExpireAsync(req.Id, now, ct))
                {
                    await notifier.NotifyExpiredAsync(req.ClientId, req.Id, now, ct);
                    _logger.LogInformation(
                        "Request {RequestId} expired after {WindowMinutes}m without accepted offer",
                        req.Id,
                        opts.ExpiryWindow.TotalMinutes);
                }
                continue;
            }

            // 10-min nudge: only fires while the request is still strictly
            // pending (no Jeeber match yet). Once an offer-service candidate
            // pool has been notified the status moves to 'matched' and the
            // nudge is no longer relevant.
            if (req.Status != RequestStatus.Pending) continue;

            if (await store.MarkNudgedAsync(req.Id, now, ct))
            {
                await notifier.NotifyTryExpandTierAsync(req.ClientId, req.Id, now, ct);
                _logger.LogInformation(
                    "Request {RequestId} hit {WindowMinutes}m no-offer mark — sent try-expanding-tier prompt",
                    req.Id,
                    opts.NoOfferNudgeWindow.TotalMinutes);
            }
        }
    }
}
