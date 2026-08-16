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

    public static readonly Counter<long> CdnUploadTicketOperations =
        Meter.CreateCounter<long>("cdn.upload_ticket.operations",
            description: "CDN upload-ticket reservation, replay, collision, and mint outcomes.");

    public static readonly Counter<long> CaseRecoveryOperations =
        Meter.CreateCounter<long>("case.recovery.operations",
            description: "Admin case-callback and push-dispatch recovery operation outcomes.");

    // gwdbx W2-R11: the ledger belongs to settlement-service, and the reconciler is deleted.
    // This counter is the observable trace of a settle the completion legs swallowed.
    public static readonly Counter<long> SettlementLedgerPostFailures =
        Meter.CreateCounter<long>("settlement.ledger.post_failures",
            description: "Number of settle calls (completion credit + AtDoor pending intent) that did not reach settlement-service and were swallowed so the delivery flow could continue. Recovery is the other completion leg, the receipt-read self-heal, or a manual settle — nothing replays them automatically.");

    // O1 (owner ruling 2026-08-16). Booked-and-never-collected went unnoticed for 81 deliveries
    // because nothing counted it; every outcome below is emitted, including the disabled one.
    public static readonly Counter<long> CommissionCollected =
        Meter.CreateCounter<long>("settlement.commission.collected",
            description: "Platform fees actually debited from a jeeber fee wallet into the platform wallet.");

    public static readonly Counter<long> CommissionCollectionSkipped =
        Meter.CreateCounter<long>("settlement.commission.skipped",
            description: "Settled deliveries whose fee was BOOKED but deliberately not collected because CommissionCollection:Enabled is false. A non-zero steady state means the owner gate is still shut.");

    public static readonly Counter<long> CommissionCollectionInsufficient =
        Meter.CreateCounter<long>("settlement.commission.insufficient",
            description: "Settled deliveries whose fee could not be taken because the jeeber's fee wallet did not cover it. The delivery stays settled; the fee is a debt.");

    public static readonly Counter<long> CommissionCollectionUncertain =
        Meter.CreateCounter<long>("settlement.commission.uncertain",
            description: "Commission debits whose execute was ambiguous. Deliberately not aborted and not stamped; re-driving replays the same idempotency key.");

    public static readonly Counter<long> CommissionCollectionFailures =
        Meter.CreateCounter<long>("settlement.commission.failures",
            description: "Commission debits that deterministically failed before any money moved (no fee wallet, no platform wallet, initiate rejected).");

    public static readonly Counter<long> CommissionStampFailures =
        Meter.CreateCounter<long>("settlement.commission.stamp_failures",
            description: "Fees that WERE collected but whose wallet transaction id could not be stamped onto the settlement row. Reconcile from the wallet ledger.");
}
