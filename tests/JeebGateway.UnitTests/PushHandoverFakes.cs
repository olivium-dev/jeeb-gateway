using System.Diagnostics.Metrics;
using JeebGateway.Notifications;
using JeebGateway.Observability;
using Microsoft.Extensions.Logging;

namespace JeebGateway.UnitTests;

// One scripted IGenericEventDispatcher: return any classification, or throw.
public sealed class ScriptedEventDispatcher : IGenericEventDispatcher
{
    private readonly GenericEventDispatchClassification? _classification;
    private readonly Exception? _throw;

    public ScriptedEventDispatcher(GenericEventDispatchClassification classification)
        => _classification = classification;

    public ScriptedEventDispatcher(Exception toThrow) => _throw = toThrow;

    public List<(string EventType, string Receiver, string EntityId, string Title, string Body,
        IReadOnlyDictionary<string, string> Data, string Category)> Calls { get; } = new();

    public Task<GenericEventDispatchOutcome> DispatchAsync(
        string eventType, string receiver, string entityId, string title, string body,
        IReadOnlyDictionary<string, string> data, string refreshCategory, CancellationToken ct)
    {
        Calls.Add((eventType, receiver, entityId, title, body, data, refreshCategory));
        if (_throw is not null) throw _throw;
        return Task.FromResult(new GenericEventDispatchOutcome(_classification!.Value, null));
    }
}

// Captures log records so "was the failure reported?" is an assertion, not a hope.
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Records { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Records.Add((logLevel, formatter(state, exception), exception));

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Errors =>
        Records.Where(r => r.Level == LogLevel.Error).ToList();

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

// Deterministic capture of the business-outcome meter at Add() time.
public sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value)> _measurements = new();

    public MeterCapture()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_measurements) _measurements.Add((instrument.Name, value));
        });
        _listener.Start();
    }

    public long UnproducedTotal()
    {
        lock (_measurements)
            return _measurements
                .Where(m => m.Instrument == "notif.push_handover.unproduced")
                .Sum(m => m.Value);
    }

    public void Dispose() => _listener.Dispose();
}
