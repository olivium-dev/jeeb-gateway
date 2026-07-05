using System.Data;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Users;

/// <summary>
/// Postgres-backed <see cref="IFinancialLedgerAnonymizer"/> (JEBV4-154, AUDIT-A
/// IN-MEM-LIVE durability follow-up — the highest-risk remaining in-memory store,
/// money + GDPR).
///
/// <para>Replaces <see cref="InMemoryFinancialLedger"/> in production. The gateway's
/// financial-ledger anonymization bookkeeping — a single retained-row counter per
/// "owner" (a live user id, or an anonymized user hash once pseudonymized) — used to
/// live only in process memory and evaporated on every restart / replica move,
/// silently losing the record of which financial rows had already been anonymized for
/// a deleted user. This store persists it to the <c>financial_ledger_anonymization</c>
/// table (migration 0030).</para>
///
/// <para>Semantics are preserved BYTE-FOR-BYTE from <see cref="InMemoryFinancialLedger"/>
/// (money — amounts/keys/rounding are never changed; row_count is an integer counter
/// accumulated with plain integer addition, exactly like the in-memory
/// <c>ConcurrentDictionary&lt;string,int&gt;</c>):
/// <list type="bullet">
/// <item><see cref="AnonymizeForUserAsync"/> REMOVES the user-id key's counter and, only
/// if it existed, ADDS that count onto the hash key (accumulating onto any prior hash
/// total); returns the number of rows moved, or <c>0</c> when the user id carried no
/// rows (nothing is written to the hash key in that case). The remove-then-accumulate
/// is done inside a single serializable transaction so a concurrent anonymize can
/// neither double-move nor lose a count — the same all-or-nothing the in-memory
/// <c>TryRemove</c> + <c>AddOrUpdate</c> gave under the CLR's per-key atomicity.</item>
/// <item><see cref="CountRowsForUserAsync"/> / <see cref="CountRowsForHashAsync"/> return
/// the counter for the given key, or <c>0</c> when the key is absent — identical to
/// <c>TryGetValue</c> defaulting to 0.</item>
/// <item><see cref="Seed"/> ADDS N rows onto the owner key (accumulating), matching the
/// in-memory test/seed helper's <c>AddOrUpdate</c>.</item>
/// </list></para>
/// </summary>
public sealed class PostgresFinancialLedger : IFinancialLedgerAnonymizer
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresFinancialLedger> _log;

    public PostgresFinancialLedger(INpgsqlConnectionFactory db, ILogger<PostgresFinancialLedger> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<int> AnonymizeForUserAsync(string userId, string anonymizedHash, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Serializable so the remove-and-accumulate is atomic against a concurrent
        // anonymize of the same user (mirrors the in-memory TryRemove/AddOrUpdate
        // per-key atomicity). Money/GDPR bookkeeping — never double-move, never lose.
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Remove the user-id key's counter and capture what it held. If the key was
        // absent, DELETE ... RETURNING yields no row and we move nothing (return 0),
        // exactly like `if (!_rowsByOwner.TryRemove(userId, out var rows)) return 0;`.
        int movedRows;
        const string deleteSql = """
            DELETE FROM financial_ledger_anonymization
            WHERE owner_key = @UserId
            RETURNING row_count
            """;
        await using (var del = new NpgsqlCommand(deleteSql, conn, tx))
        {
            del.Parameters.AddWithValue("UserId", userId);
            var scalar = await del.ExecuteScalarAsync(ct);
            if (scalar is null || scalar is DBNull)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }
            movedRows = Convert.ToInt32(scalar);
        }

        // Accumulate the moved rows onto the hash key (create it if new), matching
        // `_rowsByOwner.AddOrUpdate(anonymizedHash, rows, (_, e) => e + rows)`.
        const string upsertSql = """
            INSERT INTO financial_ledger_anonymization (owner_key, row_count, updated_at)
            VALUES (@Hash, @Rows, now())
            ON CONFLICT (owner_key)
            DO UPDATE SET row_count = financial_ledger_anonymization.row_count + EXCLUDED.row_count,
                          updated_at = now()
            """;
        await using (var up = new NpgsqlCommand(upsertSql, conn, tx))
        {
            up.Parameters.AddWithValue("Hash", anonymizedHash);
            up.Parameters.AddWithValue("Rows", movedRows);
            await up.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _log.LogInformation(
            "Financial-ledger anonymization moved {Rows} retained row(s) from user key to anonymized hash.",
            movedRows);
        return movedRows;
    }

    public async Task<int> CountRowsForUserAsync(string userId, CancellationToken ct)
        => await CountForKeyAsync(userId, ct);

    public async Task<int> CountRowsForHashAsync(string anonymizedHash, CancellationToken ct)
        => await CountForKeyAsync(anonymizedHash, ct);

    /// <summary>Test/seed helper — ADDS N rows onto the owner key (accumulating),
    /// mirroring <see cref="InMemoryFinancialLedger.Seed"/>.</summary>
    public async Task SeedAsync(string ownerKey, int rows, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO financial_ledger_anonymization (owner_key, row_count, updated_at)
            VALUES (@Key, @Rows, now())
            ON CONFLICT (owner_key)
            DO UPDATE SET row_count = financial_ledger_anonymization.row_count + EXCLUDED.row_count,
                          updated_at = now()
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Key", ownerKey);
        cmd.Parameters.AddWithValue("Rows", rows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> CountForKeyAsync(string ownerKey, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT row_count FROM financial_ledger_anonymization WHERE owner_key = @Key LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Key", ownerKey);
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is null || scalar is DBNull ? 0 : Convert.ToInt32(scalar);
    }
}
