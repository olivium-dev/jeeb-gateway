using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// Activity source and meter for T-BE-028 / JEB-64 dispute cases.
///
/// AC5 (observability) — every escalate fires <c>dispute.opened</c>
/// and every resolve fires <c>dispute.resolved</c> with the caseId as a
/// tag and the elapsed open-time as a histogram observation, both on the
/// dedicated Activity source so traces aggregate per-operation in Grafana.
/// </summary>
public static class DisputeCaseTelemetry
{
    public const string ActivitySourceName = "Jeeb.Disputes.V2";
    public const string MeterName = "Jeeb.Disputes.V2";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Open-path latency histogram. Buckets are aligned with the AC6
    /// 1-second budget so the p99 alert lands on a real bucket boundary.
    /// </summary>
    public static readonly Histogram<double> OpenDurationMs =
        Meter.CreateHistogram<double>("jeeb.dispute.case.open.ms", unit: "ms",
            description: "Wall-clock time spent in the escalate orchestration (T-BE-028 AC6).");

    public static readonly Histogram<double> ResolveDurationMs =
        Meter.CreateHistogram<double>("jeeb.dispute.case.resolve.ms", unit: "ms",
            description: "Wall-clock time spent in the resolve orchestration (T-BE-028).");

    public static readonly Counter<long> Opened =
        Meter.CreateCounter<long>("jeeb.dispute.case.opened",
            description: "Number of dispute cases opened via /v1/deliveries/{id}/escalate (AC5).");

    public static readonly Counter<long> Resolved =
        Meter.CreateCounter<long>("jeeb.dispute.case.resolved",
            description: "Number of dispute cases resolved via /admin/v1/disputes/{id}/resolve (AC5).");

    public static readonly Counter<long> EvidenceDegraded =
        Meter.CreateCounter<long>("jeeb.dispute.case.evidence_degraded",
            description: "Cases opened with a degraded evidence bundle (chat or gps timeout).");

    public static readonly Counter<long> RefundFailures =
        Meter.CreateCounter<long>("jeeb.dispute.case.refund_failures",
            description: "Refund calls to unified_payment_gateway that failed during resolve.");
}
