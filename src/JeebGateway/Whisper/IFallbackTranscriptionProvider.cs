namespace JeebGateway.Whisper;

/// <summary>
/// Secondary transcription provider invoked when the primary Whisper API
/// is unavailable (circuit open or retries exhausted). Implementations
/// may call a different STT engine (Google Cloud Speech, Azure Cognitive
/// Services, etc.) or return a degraded "queued" result.
/// </summary>
public interface IFallbackTranscriptionProvider
{
    bool IsAvailable { get; }
    Task<WhisperTranscription?> TranscribeAsync(WhisperAudio audio, CancellationToken ct);
}

/// <summary>
/// No-op fallback that always returns null — the caller will queue the audio
/// for async retry. Replace with a real secondary STT provider in production.
/// </summary>
public sealed class NoOpFallbackTranscriptionProvider : IFallbackTranscriptionProvider
{
    public bool IsAvailable => false;
    public Task<WhisperTranscription?> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
        => Task.FromResult<WhisperTranscription?>(null);
}
