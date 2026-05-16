namespace JeebGateway.Whisper;

public sealed record WhisperAudio(
    byte[] Content,
    string FileName,
    string ContentType);

public sealed record WhisperTranscription(string Text, string Language);

public enum TranscriptionOutcome
{
    Transcribed,
    QueuedForRetry
}

public sealed record TranscriptionResult(
    string AudioId,
    TranscriptionOutcome Outcome,
    WhisperTranscription? Transcription,
    string? Reason);

public sealed class TranscribeRequest
{
    public string FileName { get; set; } = "audio";
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Base64-encoded audio bytes for the MVP JSON-only contract.</summary>
    public string AudioBase64 { get; set; } = string.Empty;
}

public sealed record TranscribeResponse(
    string AudioId,
    string Status,
    string? Transcription,
    string? Language,
    string? Reason);
