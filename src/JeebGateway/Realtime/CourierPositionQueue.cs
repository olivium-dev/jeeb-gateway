using System;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime;

/// <summary>
/// One courier fix, handed from the GPS ingest hot path to the background publisher.
/// Immutable, and carries only value types and strings: NO
/// <see cref="System.Threading.CancellationToken"/>, NO scoped service, NO
/// <c>HttpContext</c> — so it is safe to outlive the request scope that created it.
/// </summary>
public sealed record CourierPosition(
    string DeliveryId,
    string JeeberId,
    double Lat,
    double Lng,
    double? Accuracy,
    DateTimeOffset DeviceTimestamp);

/// <summary>
/// The dispatch buffer between <c>POST /location/update</c> returning 200 and the
/// realtime publish. Not a store of record: a fix lost on restart or on overflow is a
/// position the customer's map simply does not receive, and the next fix (one second
/// later) supersedes it. The authoritative write is
/// <see cref="JeebGateway.Tracking.ILocationStore"/>, which happens first and
/// synchronously.
/// </summary>
public interface ICourierPositionQueue
{
    /// <summary>
    /// Never blocks, never throws. Returns <c>false</c> when the buffer is full — the
    /// fix is dropped and the caller logs it.
    /// </summary>
    bool TryEnqueue(CourierPosition position);

    ChannelReader<CourierPosition> Reader { get; }

    int PendingCount { get; }
}

/// <inheritdoc />
// gwdbx PLAN §3-C: retained transient buffer, no authoritative state, restart-drop benign —
// NOT a durable store, never on the StoreDurabilityGuard roster. docs/runbooks/gwdbx-program-rules.md
public sealed class CourierPositionQueue : ICourierPositionQueue
{
    public CourierPositionQueue(IOptions<CourierPositionPublishOptions> options)
        : this(options.Value.QueueCapacity)
    {
    }

    public CourierPositionQueue(int capacity)
    {
        // BoundedChannelFullMode.Wait + TryWrite is the non-blocking, OBSERVABLE overflow
        // shape (the NewRequestFanoutQueue precedent): TryWrite returns false immediately
        // when the buffer is full — only WriteAsync would ever wait — which is what lets
        // the caller log the drop. DropWrite would discard AND return true, hiding the
        // overflow from operators.
        _channel = Channel.CreateBounded<CourierPosition>(
            new BoundedChannelOptions(Math.Max(1, capacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    private readonly Channel<CourierPosition> _channel;

    public ChannelReader<CourierPosition> Reader => _channel.Reader;

    public int PendingCount => _channel.Reader.Count;

    public bool TryEnqueue(CourierPosition position)
        => position is not null && _channel.Writer.TryWrite(position);
}

/// <summary>Binds <c>Tracking:RealtimePublish</c>.</summary>
public sealed class CourierPositionPublishOptions
{
    public const string SectionName = "Tracking:RealtimePublish";

    /// <summary>
    /// Master switch. Off by default so a deployment without a Guardian secret — or
    /// without the realtime service — behaves exactly as it did before this path existed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Buffered fixes. At one fix per second per active courier this absorbs a
    /// multi-second realtime stall across a large fleet before it starts shedding.
    /// </summary>
    public int QueueCapacity { get; set; } = 2048;

    /// <summary>
    /// Hard ceiling on a single publish. Bounds how long a wedged realtime service can
    /// hold the drain loop; the resilience pipeline's own timeout sits under this.
    /// </summary>
    public int PublishTimeoutMs { get; set; } = 3000;

    /// <summary>Maximum retries after a realtime 429 response.</summary>
    public int MaxThrottleRetries { get; set; } = 2;

    /// <summary>Delay used when a 429 omits both Retry-After and its JSON retry hint.</summary>
    public int ThrottleFallbackDelayMs { get; set; } = 1000;

    /// <summary>Upper bound for one server-requested throttle delay.</summary>
    public int MaxThrottleDelayMs { get; set; } = 6000;

    /// <summary>Bounded parallelism across independent delivery topics.</summary>
    public int MaxParallelPublishes { get; set; } = 16;
}
