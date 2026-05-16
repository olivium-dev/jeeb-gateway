namespace JeebGateway.Whisper;

/// <summary>
/// Reserved for transient Whisper outage cases — network errors, timeouts, 5xx, rate limits.
/// Triggers retry, then circuit-breaker, then fallback to audio-only mode.
/// Programmer errors (4xx auth/validation) propagate as-is.
/// </summary>
public sealed class WhisperUnavailableException : Exception
{
    public WhisperUnavailableException(string message) : base(message) { }
    public WhisperUnavailableException(string message, Exception inner) : base(message, inner) { }
}
