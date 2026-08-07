using System.Collections.Concurrent;

namespace JeebGateway.Users;

/// <summary>
/// Legacy account-deletion contract retained for isolated compatibility tests.
/// The gateway owns no financial ledger. Durable COD records and their retention
/// or pseudonymization policy belong to unified-payment-gateway.
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
/// Test-only stand-in. Production CMS routes never use this process-local fixture.
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
