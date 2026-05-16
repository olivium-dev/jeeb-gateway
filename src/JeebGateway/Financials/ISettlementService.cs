namespace JeebGateway.Financials;

/// <summary>
/// Cash settlement orchestration (T-backend-016 / JEEB-34).
///
/// Responsible for:
/// <list type="number">
///   <item>Resolving the delivery row + authorization (only the assigned
///         Jeeber settles).</item>
///   <item>Validating the row is in <c>delivered</c> (post-OTP handover).</item>
///   <item>Re-computing fees via <see cref="CommissionCalculator"/> from the
///         row's tier — the caller never gets to pick the rate.</item>
///   <item>Posting a ledger entry to wallet-service.</item>
///   <item>Persisting the settlement so the receipt endpoint can render it.</item>
/// </list>
///
/// All Jeeb business logic lives here; downstream services only see the
/// generic ledger primitive.
/// </summary>
public interface ISettlementService
{
    Task<SettlementResult> SettleAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsJeeber,
        SettleDeliveryRequest body,
        CancellationToken ct);

    Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct);
}
