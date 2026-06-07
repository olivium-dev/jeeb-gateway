using Microsoft.Extensions.Options;

namespace JeebGateway.Whisper;

/// <summary>
/// Config-gated, network-free <see cref="IWhisperClient"/> used for local dev and CI
/// where no OpenAI API key is available. It NEVER calls an external service; it returns
/// a deterministic transcript so the rest of the voice-ordering pipeline
/// (<see cref="ResilientTranscriptionService"/>, circuit breaker, fallback queue,
/// <c>RequestVoiceController</c>) can be exercised end-to-end without a live key.
///
/// This client is selected by the seam in Program.cs when either
/// <see cref="WhisperOptions.FakeTranscribe"/> is <c>true</c> OR no
/// <see cref="WhisperOptions.ApiKey"/> is configured. The real
/// <see cref="WhisperClient"/> is retained unchanged and is the production path.
///
/// THIN BFF: the transcript byte-matches the voice-service contract
/// (<see cref="WhisperTranscription"/>); there is no STT business logic here beyond
/// producing a placeholder string in the configured language.
/// </summary>
public sealed class FakeWhisperClient : IWhisperClient
{
    private readonly WhisperOptions _options;

    public FakeWhisperClient(IOptions<WhisperOptions> options)
    {
        _options = options.Value;
    }

    public Task<WhisperTranscription> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Deterministic, non-empty transcript keyed off the clip metadata so callers
        // and tests can assert a stable shape. Confidence 1.0 marks the synthetic origin.
        var text = $"[fake-transcript] {audio.FileName} ({audio.Content.Length} bytes)";
        return Task.FromResult(new WhisperTranscription(text, _options.Language, Confidence: 1.0));
    }
}
