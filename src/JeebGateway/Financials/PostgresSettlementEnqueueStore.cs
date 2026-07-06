using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Financials;

/// <summary>
/// Postgres-backed <see cref="ISettlementEnqueueStore"/> (JEBV4-124, AUDIT-A
/// durability guard-gap — MONEY-ADJACENT).
///
/// <para>Replaces <see cref="InMemorySettlementEnqueueStore"/> in production. The
/// pending-COD-settlement enqueue intent — "this delivery reached handover-complete
/// and had a settlement enqueued" — used to live only in a gateway-process
/// <c>ConcurrentDictionary&lt;string,DateTimeOffset&gt;</c> and evaporated on every
/// restart / replica move, silently losing the record of which deliveries had already
/// been enqueued. Because the store's whole contract is idempotency ("no
/// double-enqueue"), losing it in a money path risks a duplicate settlement enqueue
/// after a bounce — so it is guarded fail-closed in prod-like environments
/// (<see cref="JeebGateway.Infrastructure.StoreDurabilityGuard"/>). This store persists
/// it to the <c>settlement_enqueue</c> table (migration 0034).</para>
///
/// <para>Semantics are preserved exactly from <see cref="InMemorySettlementEnqueueStore"/>:
/// <list type="bullet">
/// <item><see cref="TryEnqueueAsync"/> — idempotent on <c>delivery_id</c> (the PRIMARY
/// KEY) via <c>INSERT ... ON CONFLICT (delivery_id) DO NOTHING RETURNING delivery_id</c>:
/// the FIRST call inserts and returns the row (<c>true</c>), every subsequent call for the
/// same delivery is a no-op that returns <c>false</c> and PRESERVES the original
/// <c>enqueued_at</c> — byte-for-byte the <c>ConcurrentDictionary.TryAdd</c> contract.
/// This is the same DB-level idempotency <see cref="PostgresSettlementStore"/> uses for
/// the settlement row itself (UNIQUE(delivery_id) + ON CONFLICT DO NOTHING).</item>
/// <item><see cref="IsEnqueuedAsync"/> — a bare existence probe
/// (<c>SELECT 1 ... LIMIT 1</c>), matching <c>ConcurrentDictionary.ContainsKey</c>.</item>
/// </list></para>
///
/// <para>The connection factory (<see cref="INpgsqlConnectionFactory"/>) only holds the
/// connection string, so constructing this store opens no socket — resolving the
/// singleton is side-effect-free.</para>
/// </summary>
public sealed class PostgresSettlementEnqueueStore : ISettlementEnqueueStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresSettlementEnqueueStore> _log;

    public PostgresSettlementEnqueueStore(
        INpgsqlConnectionFactory db,
        ILogger<PostgresSettlementEnqueueStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<bool> TryEnqueueAsync(string deliveryId, DateTimeOffset at, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
            throw new ArgumentException("deliveryId required", nameof(deliveryId));

        await using var conn = await _db.OpenAsync(ct);

        // Idempotent enqueue: the FIRST insert for a delivery returns its id (→ true);
        // a duplicate hits the PK conflict, inserts nothing, and RETURNING yields no row
        // (→ false), preserving the original enqueued_at. Money path — no double-enqueue.
        const string sql = """
            INSERT INTO settlement_enqueue (delivery_id, enqueued_at, created_at)
            VALUES (@DeliveryId, @EnqueuedAt, now())
            ON CONFLICT (delivery_id) DO NOTHING
            RETURNING delivery_id
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("DeliveryId", deliveryId);
        cmd.Parameters.AddWithValue("EnqueuedAt", at);

        var inserted = await cmd.ExecuteScalarAsync(ct) is not null;
        if (inserted)
        {
            _log.LogInformation(
                "Settlement enqueue recorded deliveryId={DeliveryId} at={EnqueuedAt}",
                deliveryId, at);
        }

        return inserted;
    }

    public async Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
            return false;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT 1 FROM settlement_enqueue WHERE delivery_id = @DeliveryId LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("DeliveryId", deliveryId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
