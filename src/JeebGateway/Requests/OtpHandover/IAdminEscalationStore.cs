namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Storage abstraction for admin escalation rows produced by the OTP
/// handover flow (T-backend-015). The MVP uses
/// <see cref="InMemoryAdminEscalationStore"/>; production wiring proxies
/// to the admin-service via an NSwag-generated client, backed by a
/// Postgres table colocated with <c>admin_actions</c>.
/// </summary>
public interface IAdminEscalationStore
{
    /// <summary>
    /// Persists a new escalation row. The store does NOT enforce a
    /// per-delivery uniqueness invariant — callers
    /// (<see cref="InMemoryRequestsStore.TryVerifyOtpAsync"/> and
    /// <c>OtpHandoverSweeper</c>) use the write-once
    /// <c>DeliveryRequest.OtpEscalationId</c> field for that.
    /// </summary>
    Task<AdminEscalation> CreateAsync(AdminEscalation entry, CancellationToken ct);

    /// <summary>
    /// Returns the escalation for <paramref name="deliveryId"/> matching
    /// <paramref name="reason"/>, or null when none exists. Used by the
    /// integration tests to assert the escalation was opened.
    /// </summary>
    Task<AdminEscalation?> GetForDeliveryAsync(string deliveryId, string reason, CancellationToken ct);

    /// <summary>
    /// Snapshot of every escalation row in the store. Powers the admin
    /// triage queue (production swap paginates).
    /// </summary>
    Task<IReadOnlyList<AdminEscalation>> ListAsync(CancellationToken ct);
}
