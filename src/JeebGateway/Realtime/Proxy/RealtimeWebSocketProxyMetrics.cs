using System.Diagnostics.Metrics;

namespace JeebGateway.Realtime.Proxy;

/// <summary>
/// Bounded-cardinality proxy outcomes. No URI, query, token, topic, client IP,
/// user identity, or exception value is ever attached to these measurements.
/// </summary>
public sealed class RealtimeWebSocketProxyMetrics : IDisposable
{
    public const string MeterName = "JeebGateway.RealtimeProxy";
    public const string CounterName = "jeeb_gateway_realtime_proxy_requests_total";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _requests;

    public RealtimeWebSocketProxyMetrics()
    {
        _requests = _meter.CreateCounter<long>(
            CounterName,
            description: "Realtime WebSocket proxy requests by fixed outcome.");
    }

    public void Record(string outcome) =>
        _requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => _meter.Dispose();
}
