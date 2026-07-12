namespace JeebGateway.Financials;

/// <summary>
/// COD settlement platform states (JEB-56, TL-PIN-JEB-486).
/// These are DISTINCT from the delivery-level states in <see cref="SettlementState"/>
/// (pending_settlement → settled → receipt_generated). The platform states track
/// the batch/payout lifecycle:
///
///   recorded → batched → paid
///
/// - recorded: OTP handover complete; settlement row written idempotently.
/// - batched:  weekly cron swept this settlement into a batch window.
/// - paid:     admin marked the batch as paid (terminal state).
/// </summary>
public static class CodSettlementState
{
    public const string Recorded = "recorded";
    public const string Batched  = "batched";
    public const string Paid     = "paid";

    /// <summary>
    /// JEBV4-283: the COD states that count as EARNED commission for the jeeber.
    /// <c>recorded</c> IS included — the jeeber earns the commission the moment COD is
    /// collected at delivery completion, independent of the platform-side settlement
    /// lifecycle (<c>recorded → batched → paid</c>). Batching/paying is a downstream
    /// payout concern, not a precondition for showing the earning. This is the single
    /// source of truth shared by every earnings READ path (<c>JeebEarningsController</c>
    /// = <c>/v1/jeebers/me/earnings</c> and <c>JeebEarningsBffController</c> =
    /// <c>/v1/jeeb/earnings</c>, the path the mobile app consumes) so the two can never
    /// diverge on which settlements are earnings.
    /// </summary>
    public static readonly string[] EarningsStates = [Recorded, Batched, Paid];
}
