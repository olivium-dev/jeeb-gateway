namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) — gateway-side ledger tallying cancellations per
/// (user, ISO-week) for client soft/hard limits, and per (user, 30-day
/// window) for jeeber strike accumulation.
///
/// Production swap: a Postgres-backed implementation reading from a
/// <c>cancellation_log</c> table (see db/migrations/0014). The shape is
/// intentionally additive — no FK to <c>delivery_requests</c> so the row
/// survives even after the delivery row is anonymised by the GDPR
/// account-deletion path (T-backend-035).
/// </summary>
public interface ICancellationLogStore
{
    /// <summary>
    /// Appends a single cancellation row. Idempotency is not enforced at
    /// the store layer — the policy service guards against double-record
    /// by performing the count + write under a single critical section.
    /// </summary>
    Task RecordAsync(CancellationLogEntry entry, CancellationToken ct);

    /// <summary>
    /// Number of client cancellations <paramref name="userId"/> has
    /// already accumulated inside the ISO week containing
    /// <paramref name="at"/> (Monday 00:00 UTC → next Monday 00:00 UTC).
    /// Excludes rows where <see cref="CancellationLogEntry.Role"/> is
    /// not <c>client</c>.
    /// </summary>
    Task<int> CountClientCancellationsInWeekAsync(
        string userId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Number of jeeber strikes for <paramref name="userId"/> inside
    /// the rolling window ending at <paramref name="at"/>. Only rows
    /// where <see cref="CancellationLogEntry.Role"/> is <c>jeeber</c>
    /// AND <see cref="CancellationLogEntry.StrikeIssued"/> is true count.
    /// </summary>
    Task<int> CountJeeberStrikesInWindowAsync(
        string userId, DateTimeOffset at, TimeSpan window, CancellationToken ct);
}

/// <summary>
/// One row of the <c>cancellation_log</c> tally.
/// </summary>
public sealed record CancellationLogEntry(
    string UserId,
    string Role,
    string DeliveryId,
    DateTimeOffset At,
    bool FeeApplied,
    decimal FeeAmount,
    bool StrikeIssued,
    string? Reason);
