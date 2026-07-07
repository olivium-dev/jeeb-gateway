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
///   * the selected tier's request TTL after creation without an accepted
///     offer — moves the request to <c>expired</c>
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
        var offers = scope.ServiceProvider.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        var tiers = scope.ServiceProvider.GetRequiredService<JeebGateway.Tiers.ITiersStore>();
        var opts = _options.Value;

        var now = _clock.GetUtcNow();
        var tierTtls = await LoadTierTtlsAsync(tiers, ct);
        if (tierTtls.Count == 0)
        {
            _logger.LogError("Request expiry sweep skipped: no tier TTLs are configured");
            return;
        }

        var shortestExpiryWindow = tierTtls.Values.Min();
        var scanWindow = opts.NoOfferNudgeWindow < shortestExpiryWindow
            ? opts.NoOfferNudgeWindow
            : shortestExpiryWindow;
        var scanCutoff = now - scanWindow;

        var candidates = await store.ListPendingCreatedAtOrBeforeAsync(scanCutoff, ct);

        foreach (var req in candidates)
        {
            var expiryWindow = ResolveExpiryWindow(req, tierTtls);
            var expiryCutoff = now - expiryWindow;

            // Tier TTL expiry takes precedence: if the request is past the
            // hard window we move it to terminal and notify, then skip the
            // nudge — the Client just got the harsher "expired" push and a
            // simultaneous "try expanding tier" would be confusing.
            if (req.CreatedAt <= expiryCutoff)
            {
                if (await store.TryExpireAsync(req.Id, now, ct))
                {
                    await notifier.NotifyExpiredAsync(req.ClientId, req.Id, now, ct);

                    // Best-effort: close any still-live bids on the now-terminal request
                    // so jeebers don't hold a stale "pending" offer and a late accept
                    // can't race a dead request. The request is already durably expired;
                    // a hiccup here must never undo that, so it is swallowed.
                    try
                    {
                        var closed = await offers.ExpireForRequestAsync(req.Id, now, ct);
                        if (closed > 0)
                        {
                            _logger.LogInformation(
                                "Request {RequestId} expiry closed {ClosedCount} live offer(s)",
                                req.Id, closed);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Request {RequestId} expired but closing its live offers failed; "
                            + "offers may linger as pending until next reconcile.", req.Id);
                    }

                    _logger.LogInformation(
                        "Request {RequestId} expired after {WindowMinutes}m without accepted offer",
                        req.Id,
                        expiryWindow.TotalMinutes);
                }
                continue;
            }

            // 10-min nudge: only fires while the request is still strictly
            // pending (no Jeeber match yet). Once an offer-service candidate
            // pool has been notified the status moves to 'matched' and the
            // nudge is no longer relevant.
            if (req.Status != RequestStatus.Pending) continue;
            if (req.CreatedAt > now - opts.NoOfferNudgeWindow) continue;

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

    private async Task<IReadOnlyDictionary<string, TimeSpan>> LoadTierTtlsAsync(
        JeebGateway.Tiers.ITiersStore tiers,
        CancellationToken ct)
    {
        var catalog = await tiers.ListAsync(ct);
        return catalog
            .Where(t => t.RequestTtlSeconds > 0)
            .ToDictionary(
                t => t.Id,
                t => TimeSpan.FromSeconds(t.RequestTtlSeconds),
                StringComparer.OrdinalIgnoreCase);
    }

    private TimeSpan ResolveExpiryWindow(
        DeliveryRequest req,
        IReadOnlyDictionary<string, TimeSpan> tierTtls)
    {
        var tierId = req.TierId ?? string.Empty;
        var canonicalTierId = JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierId);

        if (!string.IsNullOrWhiteSpace(canonicalTierId)
            && tierTtls.TryGetValue(canonicalTierId, out var ttl))
        {
            return ttl;
        }

        var fallback = tierTtls.Values.Min();
        _logger.LogWarning(
            "Request {RequestId} has unknown tier {TierId}; using shortest configured tier TTL {WindowMinutes}m",
            req.Id,
            string.IsNullOrWhiteSpace(tierId) ? "<empty>" : tierId,
            fallback.TotalMinutes);
        return fallback;
    }
}
