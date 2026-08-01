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

    // The read half of DurableWriteFailures, and it was missing for a reason worth
    // recording. GET /v1/state/idempotency/by-prefix returned 404 on EVERY call for six
    // weeks. The gateway's degrade-don't-fail contract did exactly what it promises —
    // caught, logged a `warn`, served the in-memory fallback, stayed 200 — and because
    // nothing COUNTED it, nothing alerted and nobody looked. Writes had a counter the
    // whole time; reads did not, so a total read outage and a healthy read path produced
    // the same signal: silence.
    //
    // Emitted from the durable-read catch blocks that swallow a fault and return a
    // degraded answer (in-memory rows, an empty list, or null). It does NOT fire when a
    // read legitimately finds nothing — a miss is an answer, a fault is not.
    //
    // `store` carries the same bounded, literal vocabulary as the write counter, so the
    // two are directly comparable per store. NO ALERT THRESHOLD IS DEFINED HERE and none
    // should be inferred: what a healthy rate looks like, and what should page, is an
    // owner decision. This commit instruments; it does not set policy.
    public static readonly Counter<long> DurableReadFailures =
        Meter.CreateCounter<long>("durable.read_failures",
            description: "Number of handled durable READ failures that were degraded to an in-memory/empty/null answer, tagged by bounded store name. Compare against durable.write_failures on the same store; a flat zero on its own proves nothing unless the store is also being read.");

    // JEBV4-47 (M3/R7): the settlement -> UPG generic-settlement ledger post is
    // best-effort. When it fails at settle time the row persists but the ledger
    // diverges until the SettlementLedgerReconciler replays it. These counters make
    // that divergence observable (ties into JEBV4-59 business counters).
    public static readonly Counter<long> SettlementLedgerPostFailures =
        Meter.CreateCounter<long>("settlement.ledger.post_failures",
            description: "Number of settlement ledger posts that failed at settle time and were left for the reconciler.");

    public static readonly Counter<long> SettlementLedgerReconciled =
        Meter.CreateCounter<long>("settlement.ledger.reconciled",
            description: "Number of previously-unposted settlement ledger entries the reconciler successfully replayed.");
}
