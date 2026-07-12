using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.JeebWallet;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.JeebWallet;

/// <summary>
/// REALAPP fix — the read seam behind <c>GET /v1/jeeb/wallet/ledger</c>.
///
/// <para>The generic wallet-service exposes a holder-BALANCE read
/// (<c>GET /Wallet/holder/{id}/wallets</c>) and a transaction-WRITE surface
/// (<c>POST /Transaction/initiate|execute</c>), but NO transaction-LIST / ledger
/// endpoint. The ledger lives in the wallet DB's <c>transactionheader</c> +
/// <c>transactiondetails</c> tables, joined to a holder through
/// <c>wallets.holderid</c>. Until wallet-service ships a Jeeb-shaped ledger LIST
/// read, the gateway reads those rows directly (READ-ONLY) and projects them into
/// the mobile-facing ledger page. This mirrors the existing direct-Postgres seam the
/// gateway already carries for COD settlements
/// (<see cref="JeebGateway.Infrastructure.INpgsqlConnectionFactory"/> /
/// <c>PostgresSettlementStore</c>).</para>
///
/// <para>ADR-0001 spirit preserved: this is a pure, read-only projection of the
/// holder's OWN transactions — no money moves, no balance is stored, no domain rule
/// is applied here. It is request-scoped and side-effect-free.</para>
/// </summary>
public interface IJeebWalletLedgerReader
{
    /// <summary>
    /// Read one page (newest-first) of the holder's transaction ledger. Returns an
    /// EMPTY list when the holder has no wallet / no transactions (never throws on a
    /// no-data holder).
    /// </summary>
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, CancellationToken ct);
}

/// <summary>
/// Postgres-backed <see cref="IJeebWalletLedgerReader"/>. Reads the wallet DB
/// directly via the connection string at <c>WalletPostgres:ConnectionString</c>.
/// Registered only when that key is configured; otherwise the controller falls back
/// to the correctly-shaped empty page (no behaviour regression in dev/CI).
/// </summary>
public sealed class PostgresJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresJeebWalletLedgerReader> _log;

    public PostgresJeebWalletLedgerReader(string connectionString, ILogger<PostgresJeebWalletLedgerReader> log)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Wallet Postgres connection string must be configured.", nameof(connectionString));
        _connectionString = connectionString;
        _log = log;
    }

    public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, CancellationToken ct)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;
        var offset = (safePage - 1) * safeSize;

        // The holder's transactions = every transactiondetails row whose source OR
        // destination wallet belongs to the holder. sign is derived per-row: +1 when
        // the holder is the DESTINATION (credit / money in), -1 when the SOURCE
        // (debit / money out). type/ref/ts come from the transaction header.
        const string sql = """
            SELECT
                d.txid::text                                         AS id,
                COALESCE(NULLIF(h.tag, ''), 'transaction')           AS type,
                d.amount                                             AS amount,
                CASE WHEN d.destinationwalletid = ANY(@WalletIds) THEN 1 ELSE -1 END AS sign,
                COALESCE(NULLIF(h.summary, ''), NULLIF(h.notes, ''), '') AS ref,
                h.createdat                                          AS ts
            FROM transactiondetails d
            JOIN transactionheader  h ON h.txid = d.txheaderid
            WHERE d.sourcewalletid = ANY(@WalletIds)
               OR d.destinationwalletid = ANY(@WalletIds)
            ORDER BY h.createdat DESC, d.txid
            LIMIT @Limit OFFSET @Offset
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // 1) Resolve the holder's wallet ids (the join key into the transaction tables).
            var walletIds = await ReadWalletIdsAsync(conn, holderId, ct);
            if (walletIds.Count == 0) return Array.Empty<JeebWalletLedgerEntry>();

            // 2) Page the holder's transactions.
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("WalletIds", walletIds.ToArray());
            cmd.Parameters.AddWithValue("Limit", safeSize);
            cmd.Parameters.AddWithValue("Offset", offset);

            var items = new List<JeebWalletLedgerEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new JeebWalletLedgerEntry
                {
                    Id = reader.GetString(0),
                    Type = reader.GetString(1),
                    // JEBV4-49 (M4): keep money as decimal end-to-end — read the
                    // NUMERIC column straight into the decimal DTO, no (double) cast
                    // (a double cast can lose integer precision past 2^53 and
                    // reintroduce fractional artifacts on large LBP amounts).
                    Amount = reader.GetDecimal(2),
                    Sign = reader.GetInt32(3),
                    Ref = reader.GetString(4),
                    Ts = reader.GetFieldValue<DateTime>(5).ToUniversalTime().ToString("o"),
                });
            }
            return items;
        }
        catch (Exception ex)
        {
            // Graceful degrade (ADR-0001): the ledger is a non-critical read; a DB blip
            // returns the empty page the mobile parser tolerates rather than a 5xx.
            _log.LogWarning(ex, "wallet ledger read for holder {HolderId} degraded to empty", holderId);
            return Array.Empty<JeebWalletLedgerEntry>();
        }
    }

    private static async Task<List<Guid>> ReadWalletIdsAsync(NpgsqlConnection conn, Guid holderId, CancellationToken ct)
    {
        const string walletSql = "SELECT walletid FROM wallets WHERE holderid = @HolderId";
        await using var cmd = new NpgsqlCommand(walletSql, conn);
        cmd.Parameters.AddWithValue("HolderId", holderId);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }
}

/// <summary>
/// The dev/CI fallback <see cref="IJeebWalletLedgerReader"/>: returns the empty
/// ledger page the mobile parser tolerates, used when
/// <c>WalletPostgres:ConnectionString</c> is unset (no wallet DB to read). Keeps the
/// controller's dependency satisfiable in tests / local runs without Postgres —
/// identical to the pre-fix behaviour, so there is no regression when unconfigured.
/// </summary>
public sealed class NullJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JeebWalletLedgerEntry>>(Array.Empty<JeebWalletLedgerEntry>());
}
