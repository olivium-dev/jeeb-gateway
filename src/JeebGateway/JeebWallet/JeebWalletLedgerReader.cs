using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace JeebGateway.JeebWallet;

/// <summary>
/// REALAPP fix — the read seam behind <c>GET /v1/jeeb/wallet/ledger</c>.
///
/// The wallet service does not currently expose a transaction-list contract. The
/// gateway deliberately does not compensate by reaching into the wallet database;
/// callers receive an empty page until the owning service adds that read API.
/// </summary>
public interface IJeebWalletLedgerReader
{
    /// <summary>
    /// Read one page (newest-first) of the holder's transaction ledger. Returns an
    /// EMPTY list when the holder has no wallet / no transactions (never throws on a
    /// no-data holder).
    /// </summary>
    /// <param name="type">
    /// OPTIONAL exact operation-type filter, matched against the SAME string surfaced as each
    /// row's <c>type</c> (e.g. <c>partner-topup</c> / <c>partner-cash-credit</c>). <c>null</c> → no
    /// type filter; an unknown value naturally yields an empty page (a miss, never an error).
    /// </param>
    /// <param name="from">OPTIONAL inclusive lower bound (UTC calendar date); <c>null</c> → unbounded below.</param>
    /// <param name="to">OPTIONAL inclusive upper bound (UTC calendar date, the whole day); <c>null</c> → unbounded above.</param>
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to, CancellationToken ct);
}

/// <summary>
/// Fail-closed owner-contract placeholder. It holds no state and opens no database
/// connection; the existing mobile response shape remains stable while the owner
/// contract is incomplete.
/// </summary>
public sealed class NullJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JeebWalletLedgerEntry>>(Array.Empty<JeebWalletLedgerEntry>());
}
