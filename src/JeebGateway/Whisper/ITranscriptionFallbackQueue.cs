using System.Collections.Concurrent;

namespace JeebGateway.Whisper;

public sealed record QueuedTranscription(string AudioId, string Reason, DateTimeOffset QueuedAt);

public interface ITranscriptionFallbackQueue
{
    Task EnqueueAsync(QueuedTranscription item, CancellationToken ct);
    IReadOnlyCollection<QueuedTranscription> Snapshot();
}

/// <summary>
/// In-memory queue of audio to retry once Whisper recovers, wiped on every bounce. Nothing DRAINS it —
/// the only reader is WhisperHealthCheck's depth gauge — and no durable queue is wired in any environment.
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
