using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Financials;

/// <summary>
/// Postgres-backed <see cref="ISettlementLedgerClient"/> (b05/GW1 W1.8 + W3.5(b),
/// owner ruling 2026-07-31 "PROMOTE") — the durable cash-settlement ledger.
///
/// <para><b>What it replaces, and why the old rationale did not hold.</b>
/// <see cref="InMemorySettlementLedgerClient"/> argued that an in-process ledger was
/// sufficient because "SettlementService persists the settlement row as the
/// gateway-side system of record and treats the ledger post as best-effort". The
/// first half is true and unchanged. The half it missed is that the class's own
/// correctness rests on <c>_entries.GetOrAdd(request.IdempotencyKey, …)</c> — replay
/// the same key, get the ORIGINAL entry back, never post twice. That memo lived in a
/// <c>ConcurrentDictionary</c>. A restart empties it, so the very next replay of the
/// same settlement id mints a SECOND ledger entry id for one hand-to-hand cash
/// collection, and both <see cref="SettlementService"/> and
/// <see cref="SettlementLedgerReconciler"/> then stamp that second id onto
/// <c>settlements.ledger_entry_id</c>, overwriting the first. Nothing throws; the
/// books simply disagree with themselves.</para>
///
/// <para><b>The replay is not hypothetical.</b> <see cref="SettlementLedgerReconciler"/>
/// sweeps every 60 s and re-posts every settlement row whose <c>ledger_entry_id</c> is
/// NULL, using the settlement id as the idempotency key. The idempotency memo is the
/// only thing standing between that sweep and duplicate entries — and it is exactly
/// what a restart used to delete.</para>
///
/// <para><b>Semantics are preserved exactly.</b> <c>idempotency_key</c> is the PRIMARY
/// KEY of <c>settlement_ledger_entries</c> (migration 0044), so
/// <c>INSERT … ON CONFLICT (idempotency_key) DO NOTHING RETURNING …</c> gives the
/// <c>GetOrAdd</c> contract at the DB level: the first post inserts and returns its
/// freshly minted id; a replay inserts nothing and this client reads the ORIGINAL
/// <c>ledger_entry_id</c> + <c>posted_at</c> back. Same DB-level idempotency shape as
/// <see cref="PostgresSettlementStore"/> (UNIQUE(delivery_id), migration 0015) and
/// <see cref="PostgresSettlementEnqueueStore"/> (delivery_id PK, migration 0034).</para>
///
/// <para><b>Cash-only.</b> This is a local double-entry record of cash the Jeeber
/// already collected by hand. It dials nothing, and no payments gateway may be added
/// to it — Jeeb is cash-on-delivery only by locked owner directive.</para>
///
/// <para>The connection factory only holds the connection string, so constructing this
/// store opens no socket — resolving the singleton is side-effect-free.</para>
/// </summary>
public sealed class PostgresSettlementLedgerClient : ISettlementLedgerClient
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostgresSettlementLedgerClient> _log;

    public PostgresSettlementLedgerClient(
        INpgsqlConnectionFactory db,
        TimeProvider clock,
        ILogger<PostgresSettlementLedgerClient> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    public async Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey required", nameof(request));

        await using var conn = await _db.OpenAsync(ct);

        var candidateId = Guid.NewGuid().ToString();
        var postedAt = _clock.GetUtcNow();

        // MONEY PATH. The first post for this key inserts and RETURNING yields the row
        // (→ this is a new entry). A replay hits the PK conflict, inserts nothing, and
        // RETURNING yields NO row — so candidateId is discarded unused and the original
        // entry is read back below. One cash collection, one ledger entry id, forever,
        // across any number of restarts.
        const string insertSql = """
            INSERT INTO settlement_ledger_entries (
                idempotency_key, ledger_entry_id,
                delivery_id, jeeber_id, client_id, entry_type,
                goods_cost, commission, insurance, total,
                currency, payment_method,
                posted_at, created_at
            ) VALUES (
                @IdempotencyKey, @LedgerEntryId,
                @DeliveryId, @JeeberId, @ClientId, @EntryType,
                @GoodsCost, @Commission, @Insurance, @Total,
                @Currency, @PaymentMethod,
                @PostedAt, now()
            )
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING ledger_entry_id, posted_at
            """;

        await using (var insertCmd = new NpgsqlCommand(insertSql, conn))
        {
            insertCmd.Parameters.AddWithValue("IdempotencyKey", request.IdempotencyKey);
            insertCmd.Parameters.AddWithValue("LedgerEntryId", candidateId);
            insertCmd.Parameters.AddWithValue("DeliveryId", request.DeliveryId);
            insertCmd.Parameters.AddWithValue("JeeberId", request.JeeberId);
            insertCmd.Parameters.AddWithValue("ClientId", request.ClientId);
            insertCmd.Parameters.AddWithValue("EntryType", request.EntryType);
            insertCmd.Parameters.AddWithValue("GoodsCost", request.GoodsCost);
            insertCmd.Parameters.AddWithValue("Commission", request.Commission);
            insertCmd.Parameters.AddWithValue("Insurance", request.Insurance);
            insertCmd.Parameters.AddWithValue("Total", request.Total);
            insertCmd.Parameters.AddWithValue("Currency", request.Currency);
            insertCmd.Parameters.AddWithValue("PaymentMethod", request.PaymentMethod);
            insertCmd.Parameters.AddWithValue("PostedAt", postedAt);

            await using var reader = await insertCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var inserted = new LedgerEntryResponse
                {
                    LedgerEntryId = reader.GetString(0),
                    PostedAt = reader.GetFieldValue<DateTimeOffset>(1),
                };
                _log.LogInformation(
                    "Settlement ledger entry posted idempotencyKey={IdempotencyKey} deliveryId={DeliveryId} ledgerEntryId={LedgerEntryId}",
                    request.IdempotencyKey, request.DeliveryId, inserted.LedgerEntryId);
                return inserted;
            }
        }

        // Conflict — an entry for this key already exists (possibly posted before a
        // restart). Return the ORIGINAL, exactly as GetOrAdd did. No second entry.
        const string selectSql = """
            SELECT ledger_entry_id, posted_at
            FROM settlement_ledger_entries
            WHERE idempotency_key = @IdempotencyKey
            """;

        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("IdempotencyKey", request.IdempotencyKey);

        await using var existingReader = await selectCmd.ExecuteReaderAsync(ct);
        if (!await existingReader.ReadAsync(ct))
        {
            // Unreachable in practice: the insert reported a conflict, so the row is
            // there. Throwing beats returning a fabricated id — the caller treats a
            // ledger post as best-effort and the reconciler will replay it. Never
            // invent a ledger entry id for money.
            throw new InvalidOperationException(
                $"settlement_ledger_entries conflicted on idempotency key '{request.IdempotencyKey}' " +
                "but the existing row could not be read back.");
        }

        var existing = new LedgerEntryResponse
        {
            LedgerEntryId = existingReader.GetString(0),
            PostedAt = existingReader.GetFieldValue<DateTimeOffset>(1),
        };
        _log.LogDebug(
            "Settlement ledger entry already exists for idempotencyKey={IdempotencyKey}; returning original ledgerEntryId={LedgerEntryId}",
            request.IdempotencyKey, existing.LedgerEntryId);
        return existing;
    }
}
