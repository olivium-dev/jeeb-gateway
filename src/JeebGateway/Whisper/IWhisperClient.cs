namespace JeebGateway.Whisper;

public interface IWhisperClient
{
    Task<WhisperTranscription> TranscribeAsync(WhisperAudio audio, CancellationToken ct);
}
