namespace JeebGateway.Whisper;

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, CancellationToken ct);
}
