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

    /// <summary>
    /// Server-driven settlement fired at handover completion (OTP verify → Done, or
    /// the customer PATCH → Done). Credits the assigned jeeber using the
    /// SERVER-AUTHORITATIVE COD amount from the delivery row (BR-16), with no caller
    /// auth and no client-supplied amount. Idempotent / exactly-once — safe to fire
    /// on both completion legs. See the implementation for the full contract.
    /// </summary>
    Task<SettlementResult> SettleOnCompletionAsync(string deliveryId, CancellationToken ct);

    /// <summary>
    /// JEBV4-306: durably snapshots the server-authoritative COD amount into the
    /// settlement store as a pending-settlement placeholder BEFORE completion, so a
    /// gateway restart mid-delivery cannot strip the amount and settle $0. Called
    /// best-effort at the AtDoor checkpoints; idempotent and a no-op when there is no
    /// live row / no assigned jeeber / no positive fee / a settlement row already exists.
    /// Returns true only when a fresh pending snapshot was inserted.
    /// </summary>
    Task<bool> TrySnapshotPendingCodAsync(string deliveryId, CancellationToken ct);

    Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct);
}
