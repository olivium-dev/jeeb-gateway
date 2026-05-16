namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// T-backend-024 (JEEB-42): cancellation orchestrator. Owns the policy
/// table for who-can-cancel-when, drives the IRequestsStore mutations,
/// and applies the 24-hour Jeeber restriction when the 3+/7d threshold
/// trips. Returning a <see cref="CancellationResult"/> instead of throwing
/// keeps every controller error mapping in one place.
/// </summary>
public interface ICancellationService
{
    Task<CancellationResult> CancelAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsClient,
        bool callerIsJeeber,
        string? reason,
        CancellationToken ct);

    /// <summary>
    /// Lists deliveries currently in <see cref="RequestStatus.CancellationRequested"/>,
    /// oldest first. Used by the admin queue.
    /// </summary>
    Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingApprovalsAsync(
        int page, int pageSize, CancellationToken ct);

    Task<AdminCancellationDecisionResult> DecideAsync(
        string deliveryId, string action, CancellationToken ct);

    /// <summary>
    /// Per-Jeeber lifetime cancellation count, used by admin dashboards
    /// (AC: "track cancellation rate per Jeeber for admin visibility").
    /// </summary>
    Task<int> GetJeeberCancellationCountAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// Rolling 7-day cancellation count for <paramref name="jeeberId"/>
    /// relative to <paramref name="at"/>. The 3+/7d trigger evaluates
    /// against this value.
    /// </summary>
    Task<int> GetJeeberCancellationCountLast7DaysAsync(
        string jeeberId, DateTimeOffset at, CancellationToken ct);
}
