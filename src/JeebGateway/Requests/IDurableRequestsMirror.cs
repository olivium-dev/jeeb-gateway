namespace JeebGateway.Requests;

/// <summary>
/// requests-durable: the gateway-Postgres owner-list mirror for the durable
/// request/order lifecycle (<see cref="DurableRequestsStore"/>).
///
/// <para>delivery-service is the canonical row owner and the source of truth for
/// a single read (<see cref="IRequestsStore.GetAsync"/> reads through to it), but
/// it exposes NO client-scoped list endpoint. So the durable owner-list
/// (<see cref="IRequestsStore.ListForClientAsync"/>, backing <c>GET /requests</c>)
/// is served from the gateway's OWN Postgres <c>delivery_requests</c> table: the
/// create path mirrors every new request here (idempotent upsert) and the list
/// reads it back, filtered by client id. This keeps the owner-list alive across a
/// gateway bounce — the in-memory model is empty after a restart.</para>
///
/// <para>This is a GATEWAY-Postgres fallback (not an upstream call): adding a
/// client-scoped list to delivery-service is out of scope (the gateway must not
/// change a reusable microservice), so the gateway owns this slim mirror.</para>
///
/// <para>Every operation is BEST-EFFORT from the decorator's point of view: the
/// decorator wraps each call so a mirror fault never fails a create/cancel and
/// never turns a read into a 5xx. The mirror is a durability backstop, not a hard
/// dependency.</para>
/// </summary>
public interface IDurableRequestsMirror
{
    /// <summary>
    /// Idempotently mirrors a freshly created request into the gateway
    /// <c>delivery_requests</c> table (<c>ON CONFLICT (id) DO NOTHING</c>, so a
    /// retried create collapses onto the same row). The row's native
    /// <c>status</c> is written as the constant <c>'pending'</c> (constraint-safe
    /// for every create) while the real gateway status is carried in
    /// <c>gw_status</c>. No-op when the id / client id is not a UUID (the native
    /// keys are UUID) — such rows simply never enter the durable mirror.
    /// </summary>
    Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct);

    /// <summary>
    /// Reflects a committed cancel onto the mirror by updating the gateway
    /// columns only (<c>gw_status</c> + cancel audit) — the native enum/CHECK
    /// columns are left untouched so no constraint can fire. Lets a post-bounce
    /// owner-list surface the cancelled / cancellation_requested status.
    /// </summary>
    Task MarkCancelledAsync(
        string requestId,
        string gwStatus,
        string? cancelledBy,
        string? cancellationReason,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Atomically reflects an expiry onto the mirror by updating ONLY the gateway
    /// columns (<c>gw_status = 'expired'</c>, <c>gw_expired_at</c>) — the native
    /// enum/CHECK columns are left untouched so no constraint can fire. This is a
    /// DERIVED projection of upstream truth, not an authority write. Returns
    /// <see langword="true"/> only when a non-terminal mirror row transitioned;
    /// missing and already-terminal rows return <see langword="false"/> so repeated
    /// observer polls cannot trigger duplicate notifications.
    /// </summary>
    Task<bool> MarkExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct);

    /// <summary>
    /// F4: reflects an owner-list-visible lifecycle mutation (accept / status change /
    /// jeeber assignment / accepted-fee) onto the mirror by updating ONLY the gateway
    /// columns (<c>gw_status</c> / <c>gw_jeeber_id</c> / <c>gw_accepted_fee</c>) — the
    /// native enum/CHECK columns are left untouched so no constraint can fire. Each
    /// argument is optional: a <see langword="null"/> leaves that column as-is (via
    /// COALESCE), so a status-only or jeeber-only mutation touches only what changed.
    /// Lets a post-bounce owner-list surface the live accepted/in-progress status and
    /// the assigned jeeber/fee instead of the stale create-time <c>pending</c>.
    /// </summary>
    Task UpdateLifecycleAsync(
        string requestId,
        string? gwStatus,
        string? gwJeeberId,
        decimal? gwAcceptedFee,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Reads the durable owner-list for <paramref name="clientId"/> (mirror rows
    /// only), oldest-first — the same ordering as the in-memory list. Returns an
    /// empty list when the client id is not a UUID or has no mirrored rows.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// JEBV4-140: reads the durable jeeber-side list for <paramref name="jeeberId"/>
    /// (mirror rows whose <c>gw_jeeber_id</c> matches), newest-first — the same
    /// ordering as the in-memory <see cref="IRequestsStore.ListForJeeberAsync"/>.
    /// The symmetric counterpart of <see cref="ListForClientAsync"/>: without it a
    /// jeeber's accepted deliveries live only in the in-memory model and vanish on a
    /// process bounce, while the client side survives. Returns an empty list when the
    /// jeeber has no mirrored rows.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// JEBV4-248: durable SINGLE-ROW read by id (mirror rows only) — the by-id
    /// counterpart of <see cref="ListForClientAsync"/>.
    ///
    /// <para>Without it, <see cref="DurableRequestsStore.GetAsync"/> resolves a single
    /// row on an in-memory miss ONLY through delivery-service, while the owner-list
    /// (<see cref="ListForClientAsync"/>) is served from THIS mirror. Those two durable
    /// sources can disagree: a row present in the mirror but not resolvable via
    /// delivery-service (expired/purged upstream, or a transient delivery-service
    /// fault) returns 404 on <c>GET /v1/requests/{id}</c> and
    /// <c>GET /v1/offers?requestId=…</c> while the SAME row still lists 200 on
    /// <c>GET /requests</c> — the get-vs-list divergence that blocks the offer-review
    /// screen (JEBV4-248). Reading the mirror on the by-id path guarantees anything the
    /// owner-list can surface is also resolvable by id (get ⊇ list).</para>
    ///
    /// Returns <see langword="null"/> when the id is not a UUID or has no mirrored row.
    /// </summary>
    Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct);
}
