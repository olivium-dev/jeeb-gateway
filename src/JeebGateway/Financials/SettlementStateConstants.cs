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
}
