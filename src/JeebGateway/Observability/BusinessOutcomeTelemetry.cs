using System.Diagnostics.Metrics;

namespace JeebGateway.Observability;

/// <summary>
/// Business-outcome counters for security-relevant and durability-degradation
/// paths that already execute in the gateway.
/// </summary>
public static class BusinessOutcomeTelemetry
{
    public const string MeterName = "Jeeb.Gateway.Outcomes";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> OtpLockouts =
        Meter.CreateCounter<long>("auth.otp.lockouts",
            description: "Number of OTP lockouts triggered by gateway-observed verification paths.");

    public static readonly Counter<long> OtpVerifyFailures =
        Meter.CreateCounter<long>("auth.otp.verify_failures",
            description: "Number of failed OTP verification attempts observed by the gateway.");

    public static readonly Counter<long> RefreshReuseDetected =
        Meter.CreateCounter<long>("auth.refresh.reuse_detected",
            description: "Number of refresh-token reuse detections observed by the gateway.");

    public static readonly Counter<long> RefreshConcurrentGraceAccepted =
        Meter.CreateCounter<long>("auth.refresh.concurrent_grace_accepted",
            description: "Number of benign concurrent refresh double-uses accepted within the rotation grace window (JEBV4-260) — the loser's request did NOT burn the token family, so the concurrent winner's session was preserved. Watch this vs auth.refresh.reuse_detected to gauge benign-collision frequency.");

    public static readonly Counter<long> HandoverEscalations =
        Meter.CreateCounter<long>("handover.escalations",
            description: "Number of admin handover escalations triggered by the gateway.");

    public static readonly Counter<long> DurableWriteFailures =
        Meter.CreateCounter<long>("durable.write_failures",
            description: "Number of handled durable writer failures, tagged by bounded store name.");
}
