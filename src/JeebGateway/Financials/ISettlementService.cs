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
///   <item>Forwarding the authoritative COD record to unified-payment-gateway.</item>
///   <item>Reading the durable owner projection so the receipt endpoint can render it.</item>
/// </list>
///
/// Jeeb authorization and composition live here; unified-payment-gateway owns
/// settlement persistence, idempotency, audit, outbox, and scheduled work.
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

    /// <summary>
    /// Verifies an already-existing owner intent against fresh authoritative
    /// delivery and accepted-offer state. Used only for an AtDoor retry after
    /// <see cref="TrySnapshotPendingCodAsync"/> reports that it did not insert.
    /// </summary>
    Task<bool> IsAuthoritativeCodIntentAsync(
        string deliveryId,
        string? deliveryStatusBeforeTransition,
        CancellationToken ct);

    Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct);
}
