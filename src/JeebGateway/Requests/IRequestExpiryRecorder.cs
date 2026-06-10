namespace JeebGateway.Requests;

/// <summary>
/// JEB-1508: durable recorder for request-expiry events. When the
/// <see cref="RequestExpirySweeper"/> moves a request from a pre-acceptance
/// state to <c>expired</c>, it calls this recorder so the transition
/// survives a gateway bounce (in-memory-only state would be lost on restart,
/// causing the sweeper to re-fire expiry notifications or, worse, silently
/// skip already-expired rows that were never re-hydrated).
///
/// Implementations MUST be idempotent — a duplicate call for the same
/// <paramref name="requestId"/> is a no-op (the state-service idempotency
/// store enforces this server-side). Implementations MUST NOT throw:
/// a best-effort durable write must never roll back the already-committed
/// in-memory expiry transition or fail the caller.
/// </summary>
public interface IRequestExpiryRecorder
{
    /// <summary>
    /// Durably records that <paramref name="requestId"/> was expired at
    /// <paramref name="expiredAt"/>. Best-effort: degraded if the
    /// state-service is unreachable (the in-memory transition stands).
    /// </summary>
    Task RecordExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct);
}

/// <summary>
/// No-op recorder used when the durable-requests feature flag is OFF.
/// Keeps the sweeper's constructor signature stable across environments.
/// </summary>
public sealed class NoOpRequestExpiryRecorder : IRequestExpiryRecorder
{
    public static readonly NoOpRequestExpiryRecorder Instance = new();

    public Task RecordExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct)
        => Task.CompletedTask;
}
