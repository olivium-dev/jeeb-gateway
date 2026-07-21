using JeebGateway.Services.Clients;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Stateless replacement for <see cref="RequestExpirySweeper"/>. It computes no
/// TTL: delivery-service owns the tier TTL and authors the terminal transition
/// (<c>Cancelled</c> with trigger <c>tier_ttl_elapsed</c>). The gateway merely
/// observes that fact and projects it onto its own read model so
/// <c>GET /v1/requests</c> keeps returning the same <c>Expired</c> token the
/// mobile app already parses. <see cref="RequestStatus.Expired"/> is unchanged;
/// only its producer changes.
/// </summary>
public class RequestExpiryObserver : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<RequestExpiryOptions> _options;
    private readonly IOptionsMonitor<RequestExpirySourceOptions> _source;
    private readonly IDeliveryServiceClient _delivery;
    private readonly ILogger<RequestExpiryObserver> _logger;

    public RequestExpiryObserver(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<RequestExpiryOptions> options,
        IOptionsMonitor<RequestExpirySourceOptions> source,
        IDeliveryServiceClient delivery,
        ILogger<RequestExpiryObserver> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _source = source;
        _delivery = delivery;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.ObserverInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ObserveOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Request expiry observation failed");
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

    public async Task ObserveOnceAsync(CancellationToken ct)
    {
        if (!_source.CurrentValue.ObserverEnabled)
        {
            _logger.LogDebug("Request expiry observer is disabled by the TTL authority rollout switch");
            return;
        }

        var opts = _options.Value;
        var now = _clock.GetUtcNow();
        var since = now - (2 * opts.ObserverInterval);
        var rows = await _delivery.ListExpiredDeliveriesAsync(
            since,
            opts.ObserverBatchLimit,
            ct);

        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRequestExpiryNotifier>();
        var offers = scope.ServiceProvider
            .GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();

        var projected = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.DeliveryId))
            {
                continue;
            }

            try
            {
                var expiredAt = row.ExpiredAt == default ? now : row.ExpiredAt;
                if (!await store.TryExpireAsync(row.DeliveryId, expiredAt, ct))
                {
                    continue;
                }

                projected++;

                var suppress = opts.SuppressNotifyBefore is { } cut && expiredAt < cut;
                if (!suppress)
                {
                    var clientId = row.ClientId;
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        clientId = (await store.GetAsync(row.DeliveryId, ct))?.ClientId;
                    }

                    if (!string.IsNullOrWhiteSpace(clientId))
                    {
                        await notifier.NotifyExpiredAsync(
                            clientId,
                            row.DeliveryId,
                            expiredAt,
                            ct);
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Request {RequestId} expiry push was suppressed by the historical backfill configuration",
                        row.DeliveryId);
                }

                try
                {
                    var closed = await offers.ExpireForRequestAsync(
                        row.DeliveryId,
                        expiredAt,
                        ct);
                    if (closed > 0)
                    {
                        _logger.LogInformation(
                            "Request {RequestId} expiry closed {ClosedCount} live offer(s)",
                            row.DeliveryId,
                            closed);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Request {RequestId} expired but closing its live offers failed; "
                        + "offers may linger as pending until next reconcile.",
                        row.DeliveryId);
                }

                _logger.LogInformation(
                    "Request {RequestId} was projected expired from upstream at {ExpiredAt}",
                    row.DeliveryId,
                    expiredAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to project upstream expiry for request {RequestId}; continuing observer pass",
                    row.DeliveryId);
            }
        }

        if (rows.Count > 0)
        {
            _logger.LogInformation(
                "Observed {ObservedCount} upstream expired request(s); projected {ProjectedCount}",
                rows.Count,
                projected);
        }
    }
}
