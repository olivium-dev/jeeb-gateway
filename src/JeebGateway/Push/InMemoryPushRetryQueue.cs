namespace JeebGateway.Push;

public sealed class InMemoryPushRetryQueue : IPushRetryQueue
{
    private readonly List<PushRetryEntry> _entries = new();
    private readonly object _lock = new();

    public int PendingCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    public Task EnqueueAsync(PushRetryEntry entry, CancellationToken ct)
    {
        lock (_lock) _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PushRetryEntry>> DrainDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_lock)
        {
            var due = _entries.Where(e => e.DueAt <= now).ToArray();
            if (due.Length > 0)
            {
                _entries.RemoveAll(e => e.DueAt <= now);
            }
            return Task.FromResult<IReadOnlyList<PushRetryEntry>>(due);
        }
    }
}
