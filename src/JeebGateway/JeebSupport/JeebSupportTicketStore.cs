using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.JeebSupport;

/// <summary>
/// Owner-scoped support-ticket persistence (JM-063), backed by
/// <b>jeeb-state-service</b> per <b>ADR-0005</b> — NOT an in-gateway in-memory
/// store. The gateway stays STATELESS/THIN (ADR-0001): every row lives behind the
/// state-service opaque KV. This is the single seam where the controller reads/writes.
/// </summary>
public interface IJeebSupportTicketStore
{
    /// <summary>Persist a new ticket row. Returns the same row.</summary>
    Task<SupportTicketRow> CreateAsync(SupportTicketRow row, CancellationToken ct);

    /// <summary>Read one ticket by id, or <c>null</c> if it does not exist.</summary>
    Task<SupportTicketRow?> GetAsync(string id, CancellationToken ct);

    /// <summary>Read all of <paramref name="ownerId"/>'s tickets (unordered; the projection sorts/pages).</summary>
    Task<IReadOnlyList<SupportTicketRow>> ListByOwnerAsync(string ownerId, CancellationToken ct);
}

/// <summary>
/// <see cref="IJeebSupportTicketStore"/> backed by the jeeb-state-service opaque KV
/// (<see cref="IIdempotencyStore"/>, the R1 surface — the ONLY general key→opaque-body
/// store with GET-by-key the state-service contract exposes; reused here exactly as
/// <c>StateServiceOfferRequestIndex</c> reuses it for durable routing pairs). The gateway
/// holds NO row itself — ADR-0001/0005 compliant.
///
/// <para><b>What is fully backed (create + read-one).</b> A ticket is one IMMUTABLE opaque
/// row keyed <c>support-ticket:{id}</c>. <see cref="CreateAsync"/> writes it via the
/// insert-once KV (<c>INSERT … ON CONFLICT (key) DO NOTHING</c>); <see cref="GetAsync"/>
/// reads it back by id. Both round-trip verbatim through jeeb-state-service and survive a
/// gateway bounce / replica move. This is the surface the mobile <c>DioSupportRepository</c>
/// actually calls today (<c>POST /v1/support/tickets</c> only).</para>
///
/// <para><b>⚠️ list-by-owner gap (BLOCKED-ESCALATE).</b> The state-service KV is GET-by-key
/// ONLY — it has NO list-by-owner / prefix-scan query, and its <c>ON CONFLICT DO NOTHING</c>
/// upsert means a MUTABLE owner-index row cannot be grown after its first write. So
/// <see cref="ListByOwnerAsync"/> cannot enumerate a caller's tickets without a NEW upstream
/// primitive (a "list state rows by owner/prefix" query on jeeb-state-service). Per ADR-0005
/// — "prefer adding that generic primitive to the state service over putting an index in the
/// gateway" — we do NOT fabricate an in-gateway index (that would violate ADR-0001/0005).
/// Until the primitive lands, <see cref="ListByOwnerAsync"/> returns the EMPTY set, which the
/// projection shapes into the cold-start empty page the mobile parser tolerates (the same
/// ADR-0001 move PR #196/#197 used for unfulfillable reads). Mobile does NOT call list today,
/// so this does not block the JM-063 submit swap.</para>
///
/// <para><b>TTL.</b> 180 days — comfortably longer than a support ticket's useful life while
/// bounding KV growth.</para>
/// </summary>
public sealed class StateServiceSupportTicketStore : IJeebSupportTicketStore
{
    internal const string TicketKeyPrefix = "support-ticket:";

    /// <summary>180-day TTL (seconds).</summary>
    internal const int TtlSeconds = 180 * 24 * 60 * 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _kv;
    private readonly ILogger<StateServiceSupportTicketStore> _logger;

    public StateServiceSupportTicketStore(IIdempotencyStore kv, ILogger<StateServiceSupportTicketStore> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    // Secondary-index key prefix: "support-ticket-owner:{ownerId}:{ticketId}".
    // Written alongside the primary "support-ticket:{id}" key so list-by-owner can
    // do a prefix scan without a full-table scan. The prefix scan uses
    // GET /v1/state/idempotency/by-prefix?prefix=support-ticket-owner:{ownerId}:
    // backed by the PK B-tree in jeeb-state-service (LIKE 'prefix%').
    internal const string OwnerKeyPrefix = "support-ticket-owner:";

    public async Task<SupportTicketRow> CreateAsync(SupportTicketRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        var body = JsonSerializer.Serialize(row, Json);
        // Primary key: used by GetAsync (by ticket id, no owner knowledge required).
        await _kv.PutOrGetAsync(TicketKeyPrefix + row.Id, statusCode: 201, body, TtlSeconds, ct);
        // Owner-index key: used by ListByOwnerAsync via prefix scan.
        // owner_id and ticket_id are both non-empty (server-minted); the colon separator
        // makes the prefix "support-ticket-owner:{ownerId}:" unambiguous.
        if (!string.IsNullOrWhiteSpace(row.UserId))
            await _kv.PutOrGetAsync(OwnerKeyPrefix + row.UserId + ":" + row.Id, statusCode: 201, body, TtlSeconds, ct);
        return row;
    }

    public async Task<SupportTicketRow?> GetAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var outcome = await _kv.GetAsync(TicketKeyPrefix + id.Trim(), ct);
        return Deserialize<SupportTicketRow>(outcome?.ResponseBodyJson);
    }

    public async Task<IReadOnlyList<SupportTicketRow>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return Array.Empty<SupportTicketRow>();

        // Prefix scan over the owner-index keys written by CreateAsync.
        // jeeb-state-service ships GET /v1/state/idempotency/by-prefix (PR #7) which
        // does WHERE key LIKE @prefix || '%' backed by the PK B-tree — no full-table scan.
        var outcomes = await _kv.FindByPrefixAsync(OwnerKeyPrefix + ownerId + ":", ct);
        return outcomes
            .Select(o => Deserialize<SupportTicketRow>(o.ResponseBodyJson))
            .OfType<SupportTicketRow>()
            .ToList();
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
