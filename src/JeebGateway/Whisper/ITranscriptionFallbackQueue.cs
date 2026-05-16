using System.Collections.Concurrent;

namespace JeebGateway.Whisper;

public sealed record QueuedTranscription(string AudioId, string Reason, DateTimeOffset QueuedAt);

public interface ITranscriptionFallbackQueue
{
    Task EnqueueAsync(QueuedTranscription item, CancellationToken ct);
    IReadOnlyCollection<QueuedTranscription> Snapshot();
}

/// <summary>
/// MVP in-memory queue for audio that needs to be retried once Whisper recovers.
/// Production wiring replaces this with a durable queue (SQS, Oban, etc.).
/// </summary>
public sealed class InMemoryTranscriptionFallbackQueue : ITranscriptionFallbackQueue
{
    private readonly ConcurrentQueue<QueuedTranscription> _items = new();

    public Task EnqueueAsync(QueuedTranscription item, CancellationToken ct)
    {
        _items.Enqueue(item);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<QueuedTranscription> Snapshot() => _items.ToArray();
}
