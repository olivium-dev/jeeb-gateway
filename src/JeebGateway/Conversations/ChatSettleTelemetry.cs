using System.Diagnostics.Metrics;
using JeebGateway.Observability;

namespace JeebGateway.Conversations;

/// <summary>
/// GW5 / W1.6-gateway — counters for the post-accept chat settlement.
///
/// <para>The whole reason GW5 exists is that this step used to fail INVISIBLY: a
/// post-commit try/catch logged one warning and swallowed, so a winner locked out of the
/// only channel a cash handover has produced no signal anyone was watching. A log line
/// nobody aggregates is not observability.</para>
///
/// <para>Emitted on <see cref="BusinessOutcomeTelemetry.MeterName"/> — the SAME meter the
/// Prometheus exporter is already wired to in <c>Program.cs</c> — following the
/// <c>NotificationRecordWriter</c> precedent, so these counters are scraped without
/// touching the exporter's meter list.</para>
///
/// <para>READ THEM AS A PAIR. <see cref="Failures"/> alone is not a health signal: zero
/// failures is indistinguishable from zero accepts. Compare against
/// <see cref="Settled"/>, which is the denominator.</para>
/// </summary>
public static class ChatSettleTelemetry
{
    private static readonly Meter Meter = new(BusinessOutcomeTelemetry.MeterName);

    /// <summary>Successful one-call seat+phase+loser-removal against chat-service.
    /// The denominator for <see cref="Failures"/>.</summary>
    public static readonly Counter<long> Settled =
        Meter.CreateCounter<long>("chat.accept_settle.settled",
            description: "Post-accept conversation settles chat-service accepted (seat + phase + loser removal in one call).");

    /// <summary>Settle attempts that raised. Every one of these is a winner who may be
    /// locked out of the delivery thread until the reconciler heals it.</summary>
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("chat.accept_settle.failures",
            description: "Post-accept conversation settle attempts that failed. Compare against chat.accept_settle.settled — a zero here with a zero there means nothing was accepted, not that nothing broke.");

    /// <summary>Accepted requests the reconciler found in a state chat-service does not
    /// agree is settled — i.e. the inline attempt was lost or never landed.</summary>
    public static readonly Counter<long> ReconcileDivergent =
        Meter.CreateCounter<long>("chat.accept_settle.reconcile_divergent",
            description: "Accepted requests a reconcile sweep found NOT settled on chat-service (wrong phase, winner not active, or a losing bidder still seated).");

    /// <summary>Divergent requests the reconciler successfully re-settled.</summary>
    public static readonly Counter<long> Reconciled =
        Meter.CreateCounter<long>("chat.accept_settle.reconciled",
            description: "Divergent accepted requests the reconcile sweep re-settled successfully.");
}
