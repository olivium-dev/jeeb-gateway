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

    public async Task<SupportTicketRow> CreateAsync(SupportTicketRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        var body = JsonSerializer.Serialize(row, Json);
        // Insert-once on the key; a fresh server-minted GUID id is always a new row.
        // statusCode is carried only so the KV row is well-formed (the routing reuses status).
        await _kv.PutOrGetAsync(TicketKeyPrefix + row.Id, statusCode: 201, body, TtlSeconds, ct);
        return row;
    }

    public async Task<SupportTicketRow?> GetAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var outcome = await _kv.GetAsync(TicketKeyPrefix + id.Trim(), ct);
        return Deserialize<SupportTicketRow>(outcome?.ResponseBodyJson);
    }

    public Task<IReadOnlyList<SupportTicketRow>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        // BLOCKED-ESCALATE: no upstream list-by-owner / prefix-scan primitive exists on
        // jeeb-state-service (the KV is GET-by-key only, insert-once). Returning the empty
        // set — the projection renders the cold-start empty page mobile tolerates — rather
        // than fabricating an in-gateway index (ADR-0001/0005). Re-point to the generic
        // primitive once jeeb-state-service ships "list state rows by owner".
        return Task.FromResult<IReadOnlyList<SupportTicketRow>>(Array.Empty<SupportTicketRow>());
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
