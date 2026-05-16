using System.Collections.Concurrent;

namespace JeebGateway.Users;

/// <summary>
/// Account-deletion seam for the financial ledger. The actual ledger
/// (db/migrations/0008) lives behind the payment/finance services; the
/// gateway only needs a contract that says "replace this user id with
/// the pseudonym on every retained row". Production wiring proxies to
/// unified_payment_gateway via an NSwag-generated client.
///
/// Returns the number of rows rewritten so the deletion store and
/// integration tests can assert "financial records retained for
/// accounting" without exposing the ledger schema to the gateway.
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
