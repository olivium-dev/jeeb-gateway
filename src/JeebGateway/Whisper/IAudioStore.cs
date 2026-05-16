namespace JeebGateway.Whisper;

public interface IAudioStore
{
    /// <summary>Persists audio so transcription can be retried later. Returns a stable audio id.</summary>
    Task<string> SaveAsync(WhisperAudio audio, CancellationToken ct);
}

/// <summary>
/// MVP in-memory store. Production wiring replaces this with S3-compatible storage that
/// matches voice-transcription-service's contract.
/// </summary>
public sealed class InMemoryAudioStore : IAudioStore
{
    private readonly Dictionary<string, WhisperAudio> _items = new();
    private readonly object _gate = new();

    public Task<string> SaveAsync(WhisperAudio audio, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        lock (_gate) { _items[id] = audio; }
        return Task.FromResult(id);
    }

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }
}
