using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Partner;

/// <summary>
/// Postgres-backed <see cref="IPartnerWalletOperationStore"/> (partner-wallet-bff money-safety
/// blocker set). Replaces <see cref="InMemoryPartnerWalletOperationStore"/> in production so the
/// idempotency dedup record — and the immutable cash-in/move audit row it doubles as — survives a
/// gateway bounce and is visible across replicas. Persists to <c>partner_wallet_operations</c>
/// (migration 0040).
///
/// <para>Reuses the sibling money-adjacent store idiom byte-for-byte: raw Npgsql over the shared
/// <see cref="INpgsqlConnectionFactory"/> (see
/// <see cref="JeebGateway.Financials.PostgresSettlementEnqueueStore"/> and
/// <see cref="JeebGateway.Admin.PostgresAdminAuditLog"/>), with DB-level idempotency via a UNIQUE
/// constraint on <c>(operation_type, actor_id, idempotency_key)</c> and
/// <c>INSERT ... ON CONFLICT DO NOTHING RETURNING id</c>: the FIRST claim inserts (Won); a duplicate
/// hits the conflict, inserts nothing, and its persisted state decides Replay vs InFlight — money can
/// never move twice for one key.</para>
/// </summary>
public sealed class PostgresPartnerWalletOperationStore : IPartnerWalletOperationStore
{
    private const string StatusPending = "pending";
    private const string StatusCompleted = "completed";
    private const string StatusUncertain = "uncertain";

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresPartnerWalletOperationStore> _log;

    public PostgresPartnerWalletOperationStore(
        INpgsqlConnectionFactory db, ILogger<PostgresPartnerWalletOperationStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<PartnerOperationClaim> TryClaimAsync(
        PartnerOperationKey key, PartnerOperationIntent intent, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // First-claim insert. ON CONFLICT DO NOTHING → a duplicate key inserts nothing and RETURNING
        // yields no row, so `won` is false and we resolve the prior row's state below.
        const string insertSql = """
            INSERT INTO partner_wallet_operations (
                id, operation_type, actor_id, idempotency_key,
                partner_id, counterparty_id, amount, evidence_note, status
            ) VALUES (
                @Id, @OperationType, @ActorId, @IdempotencyKey,
                @PartnerId, @CounterpartyId, @Amount, @EvidenceNote, @Status
            )
            ON CONFLICT (operation_type, actor_id, idempotency_key) DO NOTHING
            RETURNING id
            """;

        await using (var insertCmd = new NpgsqlCommand(insertSql, conn))
        {
            insertCmd.Parameters.AddWithValue("Id", Guid.NewGuid());
            insertCmd.Parameters.AddWithValue("OperationType", OperationTypeToken(key.Type));
            insertCmd.Parameters.AddWithValue("ActorId", key.ActorId);
            insertCmd.Parameters.AddWithValue("IdempotencyKey", key.IdempotencyKey);
            insertCmd.Parameters.AddWithValue("PartnerId", intent.PartnerId);
            insertCmd.Parameters.Add(new NpgsqlParameter("CounterpartyId", NpgsqlDbType.Uuid)
            {
                Value = (object?)intent.CounterpartyId ?? DBNull.Value,
            });
            insertCmd.Parameters.AddWithValue("Amount", intent.Amount);
            insertCmd.Parameters.AddWithValue("EvidenceNote", (object?)intent.EvidenceNote ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("Status", StatusPending);

            var won = await insertCmd.ExecuteScalarAsync(ct) is not null;
            if (won)
            {
                return new PartnerOperationClaim(PartnerClaimKind.Won, null);
            }
        }

        // Duplicate key — resolve the persisted state.
        const string selectSql = """
            SELECT status, transaction_id, amount, fees
            FROM partner_wallet_operations
            WHERE operation_type = @OperationType AND actor_id = @ActorId AND idempotency_key = @IdempotencyKey
            LIMIT 1
            """;

        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("OperationType", OperationTypeToken(key.Type));
        selectCmd.Parameters.AddWithValue("ActorId", key.ActorId);
        selectCmd.Parameters.AddWithValue("IdempotencyKey", key.IdempotencyKey);

        await using var reader = await selectCmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            // Row vanished between the failed insert and this read (a concurrent Release). Treat as
            // in-flight rather than re-executing — the safe direction for money.
            return new PartnerOperationClaim(PartnerClaimKind.InFlight, null);
        }

        var status = reader.GetString(0);
        if (!string.Equals(status, StatusCompleted, StringComparison.Ordinal))
        {
            return new PartnerOperationClaim(PartnerClaimKind.InFlight, null); // pending / uncertain
        }

        var txId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
        var amount = reader.IsDBNull(2) ? 0d : reader.GetDouble(2);
        var fees = reader.IsDBNull(3) ? 0d : reader.GetDouble(3);
        var result = new PartnerWalletMoveResponse
        {
            TransactionId = txId,
            Amount = amount,
            Fees = fees,
            Status = "executed",
        };
        return new PartnerOperationClaim(PartnerClaimKind.Replay, result);
    }

    public async Task CompleteAsync(
        PartnerOperationKey key, Guid transactionId, PartnerWalletMoveResponse result, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE partner_wallet_operations
            SET status = @Status, transaction_id = @TransactionId, amount = @Amount, fees = @Fees, updated_at = now()
            WHERE operation_type = @OperationType AND actor_id = @ActorId AND idempotency_key = @IdempotencyKey
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Status", StatusCompleted);
        cmd.Parameters.AddWithValue("TransactionId", transactionId);
        cmd.Parameters.AddWithValue("Amount", result.Amount);
        cmd.Parameters.AddWithValue("Fees", result.Fees);
        cmd.Parameters.AddWithValue("OperationType", OperationTypeToken(key.Type));
        cmd.Parameters.AddWithValue("ActorId", key.ActorId);
        cmd.Parameters.AddWithValue("IdempotencyKey", key.IdempotencyKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleaseAsync(PartnerOperationKey key, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Only free a still-pending claim; a completed/uncertain row is immutable.
        const string sql = """
            DELETE FROM partner_wallet_operations
            WHERE operation_type = @OperationType AND actor_id = @ActorId AND idempotency_key = @IdempotencyKey
              AND status = @Pending
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("OperationType", OperationTypeToken(key.Type));
        cmd.Parameters.AddWithValue("ActorId", key.ActorId);
        cmd.Parameters.AddWithValue("IdempotencyKey", key.IdempotencyKey);
        cmd.Parameters.AddWithValue("Pending", StatusPending);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkUncertainAsync(PartnerOperationKey key, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Never downgrade a completed row.
        const string sql = """
            UPDATE partner_wallet_operations
            SET status = @Uncertain, updated_at = now()
            WHERE operation_type = @OperationType AND actor_id = @ActorId AND idempotency_key = @IdempotencyKey
              AND status <> @Completed
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Uncertain", StatusUncertain);
        cmd.Parameters.AddWithValue("Completed", StatusCompleted);
        cmd.Parameters.AddWithValue("OperationType", OperationTypeToken(key.Type));
        cmd.Parameters.AddWithValue("ActorId", key.ActorId);
        cmd.Parameters.AddWithValue("IdempotencyKey", key.IdempotencyKey);
        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogError(
            "Partner wallet operation LOCKED as uncertain: type={OperationType} actor={ActorId} idem={Idem}. "
            + "A post-execute failure left the move outcome unconfirmed; it will NOT be retried automatically "
            + "(no double-move). Operator reconciliation required.",
            key.Type, key.ActorId, key.IdempotencyKey);
    }

    private static string OperationTypeToken(PartnerOperationType type) => type switch
    {
        PartnerOperationType.Topup => "topup",
        PartnerOperationType.CashCredit => "cash-credit",
        _ => type.ToString().ToLowerInvariant(),
    };
}
