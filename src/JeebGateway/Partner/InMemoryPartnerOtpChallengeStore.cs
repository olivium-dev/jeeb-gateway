using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// In-memory <see cref="IPartnerOtpChallengeStore"/> for dev/CI/test ONLY. Holds challenges in a
/// process <see cref="ConcurrentDictionary{TKey,TValue}"/> — they evaporate on restart, so this is a
/// data-loss hole for a money-authorization control and is refused fail-closed in prod-like
/// environments by <see cref="JeebGateway.Infrastructure.StoreDurabilityGuard"/> (the durable
/// <see cref="PostgresPartnerOtpChallengeStore"/> is used whenever GatewayPostgres is configured).
///
/// <para>The single-use consumption is made atomic per challenge by locking on the entry while it is
/// classified and mutated — the exact analogue of the Postgres conditional
/// <c>UPDATE ... WHERE consumed_at IS NULL RETURNING id</c>: two concurrent confirms serialize on the
/// lock, so only the first stamps consumption and the second is refused as consumed. The SHA-256 hash
/// comparison is constant-time; the raw code is never stored.</para>
/// </summary>
public sealed class InMemoryPartnerOtpChallengeStore : IPartnerOtpChallengeStore
{
    private const double AmountEpsilon = 0.00005d;

    private sealed class Entry
    {
        public Guid PartnerId { get; init; }
        public Guid JeeberId { get; init; }
        public double Amount { get; init; }
        public string CodeHash { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
        public int Attempts { get; set; }
        public DateTimeOffset? ConsumedAt { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _challenges = new();

    public Task<Guid> IssueAsync(
        Guid partnerId, Guid jeeberId, double amount, string codeHash, DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _challenges[id] = new Entry
        {
            PartnerId = partnerId,
            JeeberId = jeeberId,
            Amount = amount,
            CodeHash = codeHash,
            ExpiresAt = expiresAt,
        };
        return Task.FromResult(id);
    }

    public Task<PartnerOtpValidation> ValidateAndConsumeAsync(
        Guid challengeId, Guid partnerId, Guid jeeberId, double amount, string codeHash,
        CancellationToken ct)
    {
        if (!_challenges.TryGetValue(challengeId, out var e))
        {
            return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.NotFound, 0));
        }

        // Serialize classify + mutate on the entry so a concurrent double-submit can consume at most
        // once (the ON CONFLICT / conditional-UPDATE analogue).
        lock (e)
        {
            if (e.PartnerId != partnerId || e.JeeberId != jeeberId
                || Math.Abs(e.Amount - amount) >= AmountEpsilon)
            {
                return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.Mismatch, 0));
            }

            if (e.ConsumedAt is not null)
            {
                return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0));
            }

            if (DateTimeOffset.UtcNow >= e.ExpiresAt)
            {
                return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.Expired, 0));
            }

            if (e.Attempts >= PartnerOtpChallengePolicy.MaxAttempts)
            {
                return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.Exhausted, 0));
            }

            if (!HashEquals(e.CodeHash, codeHash))
            {
                e.Attempts++;
                var remaining = Math.Max(0, PartnerOtpChallengePolicy.MaxAttempts - e.Attempts);
                return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.WrongCode, remaining));
            }

            e.ConsumedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new PartnerOtpValidation(PartnerOtpOutcome.Valid, 0));
        }
    }

    private static bool HashEquals(string storedHash, string presentedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(presentedHash));
}
