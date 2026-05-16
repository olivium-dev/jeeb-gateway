namespace JeebGateway.Users;

/// <summary>
/// Coordinates the GDPR-style account-deletion lifecycle (T-backend-035).
/// The in-memory implementation is the MVP wiring; production will back
/// this with the <c>account_deletions</c> table in db/migrations/0010 and
/// a worker that calls <see cref="AdvanceAsync"/> periodically.
///
/// The store owns three responsibilities and nothing else:
///   1. record/return the user's current deletion state
///   2. block deletion from starting the 30-day timer while an active
///      delivery is in flight (AC: "queued, not immediate, if active
///      delivery exists")
///   3. drive the state machine forward via <see cref="AdvanceAsync"/>,
///      which is the single seam the controller, the background worker,
///      and the integration tests all share.
/// </summary>
public interface IAccountDeletionStore
{
    /// <summary>
    /// Idempotent. If the user has no record yet, creates one — either
    /// <see cref="AccountDeletionStatus.PendingActiveDelivery"/> (when
    /// <paramref name="hasActiveDelivery"/> is true) or
    /// <see cref="AccountDeletionStatus.Scheduled"/> with a 30-day
    /// <c>ScheduledPurgeAt</c>. If a record already exists, returns it
    /// unchanged so retries are safe.
    /// </summary>
    Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct);

    Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Test/worker hook. Walks every open deletion record and:
    ///   - advances <see cref="AccountDeletionStatus.PendingActiveDelivery"/>
    ///     rows whose user no longer has any active delivery to
    ///     <see cref="AccountDeletionStatus.Scheduled"/>, anonymizing the
    ///     user's orders and financial-ledger rows in the same step;
    ///   - executes the PII hard-delete on <see cref="AccountDeletionStatus.Scheduled"/>
    ///     rows whose <c>ScheduledPurgeAt</c> is at or before <paramref name="now"/>,
    ///     moving them to <see cref="AccountDeletionStatus.Completed"/>.
    /// </summary>
    Task AdvanceAsync(DateTimeOffset now, CancellationToken ct);
}
