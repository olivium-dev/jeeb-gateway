namespace JeebGateway.IntegrationTests.Whisper;

/// <summary>Minimal manually-advanced TimeProvider for deterministic breaker/backoff tests.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
