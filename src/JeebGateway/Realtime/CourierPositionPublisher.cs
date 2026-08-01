using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime;

/// <summary>
/// Drains <see cref="ICourierPositionQueue"/> and publishes each fix to
/// realtime-comunication-service, off the GPS ingest hot path.
///
/// <para><b>How "must never fail or slow the location write" is guaranteed.</b> Four
/// separate properties, none of which relies on the publish itself behaving:</para>
/// <list type="number">
///   <item><b>Different thread, later.</b> The controller only calls
///     <see cref="ICourierPositionQueue.TryEnqueue"/>, a lock-free
///     <c>Channel.Writer.TryWrite</c>. It cannot await, cannot do I/O, and returns before
///     the response is written. Nothing here runs inside the request.</item>
///   <item><b>Bounded.</b> The channel is capacity-limited, so a wedged upstream grows a
///     fixed buffer and then sheds — it can never accumulate one pending task per GPS
///     ping. Shedding is visible: <c>TryWrite</c> returns false and the caller logs it.</item>
///   <item><b>No shared cancellation.</b> Publishes run under this service's own
///     stopping token plus a per-item timeout — never the request's
///     <c>CancellationToken</c>. A client that hangs up mid-request cannot cancel, and
///     cannot surface an <see cref="OperationCanceledException"/> into, a location write
///     that already returned 200.</item>
///   <item><b>Total exception containment.</b> Every publish is wrapped; the drain loop
///     catches everything a job can throw and continues. There is no path by which a
///     realtime fault reaches the caller, because by then the caller is gone.</item>
/// </list>
///
/// <para>The publish is deliberately NOT retried. A stale position is worth less than
/// the fresh one arriving a second behind it, and the realtime service throttles the
/// <c>location</c> stream to 1 Hz anyway — a retry would spend a token on a fix the
/// service would drop.</para>
/// </summary>
public sealed class CourierPositionPublisher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ICourierPositionQueue _queue;
    private readonly CourierPositionPublishOptions _options;
    private readonly ILogger<CourierPositionPublisher> _log;

    public CourierPositionPublisher(
        IServiceProvider services,
        ICourierPositionQueue queue,
        IOptions<CourierPositionPublishOptions> options,
        ILogger<CourierPositionPublisher> log)
    {
        _services = services;
        _queue = queue;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var position in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await PublishOneAsync(position, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Property 4: the containment boundary. Nothing a publish can throw
                    // may end the drain loop, and nothing reaches the (long-gone) caller.
                    _log.LogWarning(ex,
                        "Realtime position publish for delivery {DeliveryId} threw; fix not fanned out.",
                        position.DeliveryId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Exposed so an integration test can drive a deterministic single drain.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var n = 0;
        while (_queue.Reader.TryRead(out var position))
        {
            await PublishOneAsync(position, ct);
            n++;
        }
        return n;
    }

    private async Task PublishOneAsync(CourierPosition position, CancellationToken ct)
    {
        var topic = CourierPositionTopic.For(position.DeliveryId);
        if (topic is null)
        {
            // Unreachable via the controller (it sanitizes first) but a queue is a public
            // seam; refuse rather than build a topic out of an unvalidated id.
            _log.LogWarning(
                "Delivery id {DeliveryId} is not a safe realtime topic segment; position not published.",
                position.DeliveryId);
            return;
        }

        // Property 3: this service's stopping token + a hard per-item ceiling. The
        // request's token is never in scope here — it was not captured into
        // CourierPosition, by construction.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.PublishTimeoutMs));

        // A fresh DI scope per fix: IRealtimeCommunicationClient is a typed HttpClient
        // registration (transient), so resolving it off the root provider would be a
        // captive-dependency bug.
        using var scope = _services.CreateScope();
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtimeCommunicationClient>();

        var data = new Dictionary<string, object?>
        {
            ["lat"] = position.Lat,
            ["lng"] = position.Lng,
            ["accuracy"] = position.Accuracy,
            ["deliveryId"] = position.DeliveryId,
            // The courier identity comes from the bearer at ingest, never from a body.
            ["jeeberId"] = position.JeeberId,
            ["timestamp"] = position.DeviceTimestamp.ToString("O"),
        };

        await realtime.PublishAsync(topic, CourierPositionTopic.Stream, data, meta: null, timeout.Token);
    }
}
