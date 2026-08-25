namespace JeebGateway.Whisper;

/// <summary>
/// Transient failure reported while calling the owning voice-transcription-service.
/// Provider details and credentials remain inside that service.
/// </summary>
public sealed class WhisperUnavailableException : Exception
{
    public WhisperUnavailableException(string message) : base(message) { }
    public WhisperUnavailableException(string message, Exception inner) : base(message, inner) { }
}
