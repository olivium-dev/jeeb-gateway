using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Whisper;

/// <summary>
/// Reports Whisper API availability based on the circuit breaker state
/// and the fallback provider. Registered with the "ready" tag so it
/// gates the readiness probe at <c>/health/ready</c>.
/// </summary>
public sealed class WhisperHealthCheck : IHealthCheck
{
    private readonly IWhisperCircuitBreaker _breaker;
    private readonly IFallbackTranscriptionProvider _fallbackProvider;
    private readonly ITranscriptionFallbackQueue _queue;

    public WhisperHealthCheck(
        IWhisperCircuitBreaker breaker,
        IFallbackTranscriptionProvider fallbackProvider,
        ITranscriptionFallbackQueue queue)
    {
        _breaker = breaker;
        _fallbackProvider = fallbackProvider;
        _queue = queue;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var state = _breaker.State;
        var queueDepth = _queue.Snapshot().Count;
        var data = new Dictionary<string, object>
        {
            ["circuitState"] = state.ToString(),
            ["fallbackAvailable"] = _fallbackProvider.IsAvailable,
            ["pendingQueueDepth"] = queueDepth
        };

        return Task.FromResult(state switch
        {
            CircuitState.Closed => HealthCheckResult.Healthy(
                "Whisper API is reachable", data: data),
            CircuitState.HalfOpen => HealthCheckResult.Degraded(
                "Whisper circuit half-open; probing recovery", data: data),
            CircuitState.Open => _fallbackProvider.IsAvailable
                ? HealthCheckResult.Degraded(
                    "Whisper circuit open; fallback provider active", data: data)
                : HealthCheckResult.Unhealthy(
                    "Whisper circuit open; no fallback available", data: data),
            _ => HealthCheckResult.Unhealthy("Unknown circuit state", data: data)
        });
    }
}
