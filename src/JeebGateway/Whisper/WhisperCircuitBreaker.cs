using Microsoft.Extensions.Options;

namespace JeebGateway.Whisper;

public enum CircuitState { Closed, Open, HalfOpen }

public interface IWhisperCircuitBreaker
{
    CircuitState State { get; }
    bool AllowRequest();
    void RecordSuccess();
    void RecordFailure();
}

/// <summary>
/// Trips open after <see cref="WhisperOptions.CircuitBreakerFailureThreshold"/>
/// consecutive failures and stays open for <see cref="WhisperOptions.CircuitBreakerOpenDuration"/>.
/// While open, callers must fall back to audio-only mode rather than calling Whisper.
/// </summary>
public sealed class WhisperCircuitBreaker : IWhisperCircuitBreaker
{
    private readonly WhisperOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public WhisperCircuitBreaker(IOptions<WhisperOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    public CircuitState State
    {
        get
        {
            lock (_gate) { return CurrentStateLocked(); }
        }
    }

    public bool AllowRequest()
    {
        lock (_gate)
        {
            return CurrentStateLocked() != CircuitState.Open;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerFailureThreshold && _openedAt is null)
            {
                _openedAt = _time.GetUtcNow();
            }
        }
    }

    private CircuitState CurrentStateLocked()
    {
        if (_openedAt is null) return CircuitState.Closed;
        var elapsed = _time.GetUtcNow() - _openedAt.Value;
        return elapsed >= _options.CircuitBreakerOpenDuration ? CircuitState.HalfOpen : CircuitState.Open;
    }
}
