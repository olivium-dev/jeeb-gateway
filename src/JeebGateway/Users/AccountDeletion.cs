namespace JeebGateway.Users;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Lifecycle states for an account-deletion request (T-backend-035).
/// </summary>
///   pending_active_delivery — user requested deletion, but at least one
///     delivery in <see cref="Requests.RequestStatus.ActiveStates"/> is
///     still in flight. The purge clock has NOT started. Once every
///     active delivery reaches a terminal status, the store advances
///     this row to <c>scheduled</c>.
///   scheduled — purge timer is running. Orders for this user are
///     anonymized immediately; PII fields on the profile are scheduled
///     for hard-delete at <c>ScheduledPurgeAt</c> = RequestedAt + 30 days.
///     Financial-ledger rows are anonymized on entry to this state
///     (retained for accounting per the AC) but never deleted.
///   completed — PII has been hard-deleted; the user row is left as an
///     anonymized stub so foreign keys from anonymized orders still
///     resolve.
public static class AccountDeletionStatus
{
    public const string PendingActiveDelivery = "pending_active_delivery";
    public const string Scheduled = "scheduled";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class AccountDeletionPolicy
{
    public static readonly TimeSpan PurgeDelay = TimeSpan.FromDays(30);

    public static string HashUserId(string userId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))
            .ToLowerInvariant();
}

public class AccountDeletionRequest
{
    public required string UserId { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Wall-clock time at which the PII hard-delete becomes due. Only
    /// populated once the request reaches <see cref="AccountDeletionStatus.Scheduled"/>;
    /// while we are still waiting for active deliveries it stays NULL so
    /// the 30-day window never starts on a user with in-flight orders.
    /// </summary>
    public DateTimeOffset? ScheduledPurgeAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Legacy compatibility field containing the stable pseudonym used only
    /// for gateway-owned delivery/request records. Financial owners choose
    /// and persist their own pseudonyms; this value is never sent to them.
    /// </summary>
    public required string AnonymizedUserHash { get; init; }
}
