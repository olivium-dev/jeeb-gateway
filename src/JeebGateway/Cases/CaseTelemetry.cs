using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JeebGateway.Cases;

public static class CaseTelemetry
{
    public const string ActivitySourceName = "Jeeb.Gateway.Cases";
    public const string MeterName = "Jeeb.Gateway.Cases";

    public static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "jeeb.gateway.cases.requests",
        description: "Gateway case operations by kind, operation and outcome.");

    public static readonly Counter<long> EvidencePartial = Meter.CreateCounter<long>(
        "jeeb.gateway.cases.evidence_partial",
        description: "Case evidence sources captured with an unavailable/partial marker.");

    public static readonly Counter<long> SecondaryFailures = Meter.CreateCounter<long>(
        "jeeb.gateway.cases.secondary_failures",
        description: "Optional post-commit case side effects that failed after the durable case succeeded.");

    public static readonly Counter<long> CallbackDispatches = Meter.CreateCounter<long>(
        "jeeb.gateway.cases.callback_dispatches",
        description: "State-service outbox callback dispatch outcomes.");

    public static readonly Histogram<double> UpstreamDuration = Meter.CreateHistogram<double>(
        "jeeb.gateway.cases.upstream_duration",
        unit: "ms",
        description: "State-service generic-case call duration.");
}
