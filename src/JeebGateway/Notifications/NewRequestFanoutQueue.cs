using System;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace JeebGateway.Notifications;

/// <summary>
/// P1 — the unit of work handed from the create hot path to the background fan-out.
/// Immutable, and carries only strings/doubles: NO <see cref="System.Threading.CancellationToken"/>,
/// NO scoped service, NO <c>HttpContext</c>, so it is safe to outlive the request scope.
/// </summary>
public sealed record NewRequestNotification(
    string RequestId,
    string? TierId,
    string? Description,
    string? InitiatorUserId,
    double? PickupLat,
    double? PickupLng);

/// <summary>
/// The in-memory dispatch buffer between the create 201 and the fan-out. Not a store of
/// record: losing a queued job on restart drops a best-effort push — exactly the failure
/// class a failed relay call already has today (same shape as <c>InMemoryPushRetryQueue</c>).
/// </summary>
public interface INewRequestFanoutQueue
{
    /// <summary>
    /// Never blocks, never throws. Returns <c>false</c> when the buffer is full
    /// (the job is dropped and the caller logs it).
    /// </summary>
    bool TryEnqueue(NewRequestNotification notification);

    ChannelReader<NewRequestNotification> Reader { get; }

    int PendingCount { get; }
}

/// <inheritdoc />
public sealed class NewRequestFanoutQueue : INewRequestFanoutQueue
{
    private readonly Channel<NewRequestNotification> _channel;

    public NewRequestFanoutQueue(IOptions<NewRequestFanoutOptions> options)
        : this(options.Value.QueueCapacity)
    {
    }

    public NewRequestFanoutQueue(int capacity)
    {
        // BoundedChannelFullMode.Wait + TryWrite is the non-blocking, OBSERVABLE overflow
        // shape: TryWrite returns false immediately when the buffer is full (it never waits
        // — only WriteAsync would), which is what lets the caller log the drop. DropWrite
        // would silently discard AND return true, hiding the overflow from operators.
        _channel = Channel.CreateBounded<NewRequestNotification>(
            new BoundedChannelOptions(Math.Max(1, capacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public ChannelReader<NewRequestNotification> Reader => _channel.Reader;

    public int PendingCount => _channel.Reader.Count;

    public bool TryEnqueue(NewRequestNotification notification)
        => notification is not null && _channel.Writer.TryWrite(notification);
}
