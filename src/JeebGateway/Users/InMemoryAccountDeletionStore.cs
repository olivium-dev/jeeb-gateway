using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Requests;
using JeebGateway.Tokens;

namespace JeebGateway.Users;

/// <summary>
/// In-memory implementation of the account-deletion lifecycle. Backed by
/// <see cref="IUsersStore.PurgePiiAsync"/> and
/// <see cref="IRequestsStore.AnonymizeForClientAsync"/>; the same
/// orchestration moves to a Postgres-backed worker in production
/// (db/migrations/0010).
///
/// The 30-day SLA from the acceptance criteria is encoded as a single
/// constant — production wiring will read it from configuration so legal
/// can adjust without a code change.
/// </summary>
public class InMemoryAccountDeletionStore : IAccountDeletionStore
{
    public static readonly TimeSpan PurgeDelay = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, AccountDeletionRequest> _records = new();
    private readonly object _writeLock = new();

    private readonly IUsersStore _users;
    private readonly IRequestsStore _requests;
    private readonly ITokenService _tokens;
    private readonly IFinancialLedgerAnonymizer _ledger;
    private readonly TimeProvider _clock;

    public InMemoryAccountDeletionStore(
        IUsersStore users,
        IRequestsStore requests,
        ITokenService tokens,
        IFinancialLedgerAnonymizer ledger,
        TimeProvider clock)
    {
        _users = users;
        _requests = requests;
        _tokens = tokens;
        _ledger = ledger;
        _clock = clock;
    }

    public async Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        AccountDeletionRequest record;
        bool created;
        lock (_writeLock)
        {
            if (_records.TryGetValue(userId, out var existing))
            {
                return existing;
            }

            record = new AccountDeletionRequest
            {
                UserId = userId,
                Status = hasActiveDelivery
                    ? AccountDeletionStatus.PendingActiveDelivery
                    : AccountDeletionStatus.Scheduled,
                RequestedAt = now,
                ScheduledPurgeAt = hasActiveDelivery ? null : now + PurgeDelay,
                AnonymizedUserHash = HashUserId(userId)
            };
            _records[userId] = record;
            created = true;
        }

        if (created)
        {
            // Whether or not the purge clock has started, the user has
            // asked us to delete their account — every refresh token they
            // hold must be invalidated immediately so no other device can
            // keep using the account.
            await _tokens.RevokeAllForUserAsync(userId, RevocationReason.AccountDeleted, ct);

            // If we're already in `scheduled`, anonymize the order +
            // financial ledger now so analytics joins still work but the
            // user-id linkage is gone within the same request.
            if (record.Status == AccountDeletionStatus.Scheduled)
            {
                await _requests.AnonymizeForClientAsync(userId, record.AnonymizedUserHash, ct);
                await _ledger.AnonymizeForUserAsync(userId, record.AnonymizedUserHash, ct);
            }
        }

        return record;
    }

    public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct)
    {
        _records.TryGetValue(userId, out var record);
        return Task.FromResult(record);
    }

    public async Task AdvanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Snapshot the candidates under the lock; do downstream IO outside
        // it so a slow purge can't stall RequestAsync callers.
        List<AccountDeletionRequest> toSchedule;
        List<AccountDeletionRequest> toComplete;
        lock (_writeLock)
        {
            toSchedule = _records.Values
                .Where(r => r.Status == AccountDeletionStatus.PendingActiveDelivery)
                .ToList();
            toComplete = _records.Values
                .Where(r => r.Status == AccountDeletionStatus.Scheduled
                    && r.ScheduledPurgeAt.HasValue
                    && r.ScheduledPurgeAt.Value <= now)
                .ToList();
        }

        foreach (var record in toSchedule)
        {
            var active = await _requests.CountActiveForClientAsync(record.UserId, ct);
            if (active > 0) continue;

            lock (_writeLock)
            {
                record.Status = AccountDeletionStatus.Scheduled;
                record.ScheduledPurgeAt = now + PurgeDelay;
            }

            await _requests.AnonymizeForClientAsync(record.UserId, record.AnonymizedUserHash, ct);
            await _ledger.AnonymizeForUserAsync(record.UserId, record.AnonymizedUserHash, ct);
        }

        foreach (var record in toComplete)
        {
            await _users.PurgePiiAsync(record.UserId, ct);
            lock (_writeLock)
            {
                record.Status = AccountDeletionStatus.Completed;
                record.CompletedAt = now;
            }
        }
    }

    internal static string HashUserId(string userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
