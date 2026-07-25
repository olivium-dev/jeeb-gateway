using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// P1 — drains <see cref="INewRequestFanoutQueue"/> off the create hot path and runs the
/// recipient-resolved fan-out for each job. Same shape as
/// <see cref="JeebGateway.Push.PushRetryQueueProcessor"/>: a <see cref="BackgroundService"/>
/// over <see cref="IServiceProvider"/>, plus a public single-shot drain so an integration
/// test can be deterministic instead of racing the hosted loop.
/// </summary>
public sealed class NewRequestFanoutProcessor : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly INewRequestFanoutQueue _queue;
    private readonly ILogger<NewRequestFanoutProcessor> _log;

    public NewRequestFanoutProcessor(
        IServiceProvider services,
        INewRequestFanoutQueue queue,
        ILogger<NewRequestFanoutProcessor> log)
    {
        _services = services;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunOneAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "new-request fan-out job for {RequestId} threw", job.RequestId);
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
        while (_queue.Reader.TryRead(out var job))
        {
            await RunOneAsync(job, ct);
            n++;
        }
        return n;
    }

    private async Task RunOneAsync(NewRequestNotification job, CancellationToken ct)
    {
        // A FRESH DI scope per job — INewRequestPushNotifier and ServicePushNotificationClient
        // are BOTH Scoped, so resolving them from the root provider would be a
        // captive-dependency bug.
        using var scope = _services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<INewRequestPushNotifier>();
        await notifier.FanOutAsync(job, ct);
    }
}
