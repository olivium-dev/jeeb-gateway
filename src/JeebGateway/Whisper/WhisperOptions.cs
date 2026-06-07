namespace JeebGateway.Whisper;

/// <summary>
/// Configuration for the resilient Whisper integration (T-backend-036).
/// </summary>
public sealed class WhisperOptions
{
    public const string SectionName = "Whisper";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "whisper-1";

    public string Language { get; set; } = "ar";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Dev/CI escape hatch. When <c>true</c>, the gateway uses the deterministic
    /// <see cref="FakeWhisperClient"/> instead of calling the real OpenAI Whisper API.
    /// Default <c>false</c> = REAL STT. Bound from config key <c>Whisper:FakeTranscribe</c>
    /// and additionally honored from the flat env var <c>WHISPER_FAKE_TRANSCRIBE</c>
    /// (normalized in Program.cs so the owner's named lever works as written).
    /// Note: if <see cref="FakeTranscribe"/> is <c>false</c> but no <see cref="ApiKey"/>
    /// is present, the gateway auto-falls-back to the fake client (so local/CI runs
    /// without an OpenAI key never hit the network) and logs a warning. The real
    /// path activates only when FakeTranscribe=false AND ApiKey is non-empty.
    /// </summary>
    public bool FakeTranscribe { get; set; }

    /// <summary>Per-attempt timeout. AC: 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Total attempts including the first call (so 3 = 1 try + 2 retries).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>First backoff delay; doubles on each subsequent attempt (1s → 2s → 4s).</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Consecutive failures that trip the circuit breaker open. AC: 5.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}
