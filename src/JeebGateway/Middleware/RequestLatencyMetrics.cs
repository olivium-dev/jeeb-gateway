using System.Diagnostics.Metrics;

namespace JeebGateway.Middleware;

/// <summary>
/// T-backend-050 — per-endpoint request-duration histogram.
///
/// Emits <c>jeeb_gateway_http_request_duration_seconds</c> tagged with
/// (method, route, status_class) so Prometheus / Grafana can compute
/// p50/p95/p99 per endpoint. The route tag prefers the matched route
/// template (e.g. <c>/api/requests/{id}</c>) over the raw path so the
/// cardinality of the metric stays bounded.
/// </summary>
public sealed class RequestLatencyMetrics : IDisposable
{
    public const string MeterName = "JeebGateway.Http";
    public const string HistogramName = "jeeb_gateway_http_request_duration_seconds";

    private readonly Meter _meter;
    public Histogram<double> RequestDuration { get; }

    public RequestLatencyMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        RequestDuration = _meter.CreateHistogram<double>(
            name: HistogramName,
            unit: "s",
            description: "Duration of inbound HTTP requests in seconds, tagged by route, method, and status class.");
    }

    public void Dispose() => _meter.Dispose();
}
