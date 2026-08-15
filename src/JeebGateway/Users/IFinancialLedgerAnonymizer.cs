using System.Collections.Concurrent;

namespace JeebGateway.Users;

/// <summary>
/// Account-deletion seam for the financial ledger. Wallet-service owns the
/// retained rows and chooses its own pseudonym. The gateway uses this contract
/// only to request idempotent holder closure; it stores no wallet projection.
///
/// The legacy integer result is zero when the owner does not expose row counts;
/// callers must interpret successful completion, not a positive count, as the
/// owner acknowledgement.
/// </summary>
public interface IFinancialLedgerAnonymizer
{
    Task<int> AnonymizeForUserAsync(string userId, string anonymizedHash, CancellationToken ct);

    /// <summary>
    /// Returns rows that still carry the user's id (i.e. not yet
    /// anonymized) — used by tests to confirm retention while
    /// pseudonymization is in effect.
    /// </summary>
    Task<int> CountRowsForUserAsync(string userId, CancellationToken ct);

    Task<int> CountRowsForHashAsync(string anonymizedHash, CancellationToken ct);
}

/// <summary>
/// MVP stand-in. Holds a single counter per (user id) so the deletion
/// flow has a real target to anonymize and tests can seed financial
/// rows without depending on the downstream service. Swap for the
/// unified_payment_gateway client in production.
/// </summary>
public class InMemoryFinancialLedger : IFinancialLedgerAnonymizer
{
    private readonly ConcurrentDictionary<string, int> _rowsByOwner = new();

    public Task<int> AnonymizeForUserAsync(string userId, string anonymizedHash, CancellationToken ct)
    {
        if (!_rowsByOwner.TryRemove(userId, out var rows))
        {
            return Task.FromResult(0);
        }

        _rowsByOwner.AddOrUpdate(anonymizedHash, rows, (_, existing) => existing + rows);
        return Task.FromResult(rows);
    }

    public Task<int> CountRowsForUserAsync(string userId, CancellationToken ct)
    {
        _rowsByOwner.TryGetValue(userId, out var rows);
        return Task.FromResult(rows);
    }

    public Task<int> CountRowsForHashAsync(string anonymizedHash, CancellationToken ct)
    {
        _rowsByOwner.TryGetValue(anonymizedHash, out var rows);
        return Task.FromResult(rows);
    }

    /// <summary>Test/seed helper — adds N rows owned by the user.</summary>
    public void Seed(string userId, int rows)
    {
        _rowsByOwner.AddOrUpdate(userId, rows, (_, existing) => existing + rows);
    }
}
