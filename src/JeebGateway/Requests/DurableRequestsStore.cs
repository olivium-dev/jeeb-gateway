using JeebGateway.Conversations;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Durable;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// SPINE-FOUNDATION / ADR-006: the stateless create path for the order/request
/// lifecycle. A decorator over the legacy in-memory <see cref="IRequestsStore"/>
/// that, on the CREATE methods only, additionally:
///
///  1. seeds the canonical delivery-service row via
///     <see cref="IDeliveryServiceClient.CreateDeliveryRowAsync"/>
///     (<c>POST /api/v1/deliveries</c>) — so <c>POST /matching/run</c> in
///     request_id mode resolves the row instead of 404-ing; and
///  2. records the saga in the jeeb-state-service bundle ledger via
///     <see cref="ISagaBundleRecorder"/> — so the order's lifecycle is durable
///     and the gateway holds NO order state of its own on this path.
///
/// REQUEST-ID STABILITY (the silent re-404 guard): ONE id is minted and used as
/// the in-memory row <c>Id</c>, the delivery row <c>id</c>, AND the saga bundle
/// <c>sourceId</c>. The matching run later resolves that same id.
///
/// ORDERING (BR-9 preserved, no orphan rows): the inner store's atomic
/// "count-active + insert under write lock" runs FIRST so the BR-9 cap is
/// enforced exactly as today (a <see cref="TooManyActiveRequestsException"/>
/// propagates BEFORE any upstream side-effect). Only on a successful insert are
/// the delivery row + bundle recorded. Both upstream writes are idempotent
/// (delivery: <c>ON CONFLICT (id) DO NOTHING</c>; bundle: unique
/// <c>(source, sourceId)</c>), so a retried create collapses cleanly.
///
/// THIN ORCHESTRATION: this decorator composes existing typed clients and a slim
/// gateway-Postgres owner-list mirror. It owns no status machine — the in-memory
/// inner store remains the gateway's fast read/query/sweeper model, and every
/// method not listed below delegates to it verbatim. The delivery row is the
/// matching-resolve source of truth; the bundle is the durable saga/audit trail.
///
/// requests-durable — DURABLE READS (strict superset of the in-memory behaviour):
///  * <see cref="GetAsync"/> reads the in-memory mirror first (warm = byte-for-byte
///    identical, all gateway-only fields intact) and, on a miss (a bounce /
///    cross-replica read), reads THROUGH to delivery-service so the row survives
///    a restart; a 404 / blip degrades to null (== today's unknown-id).
///  * <see cref="ListForClientAsync"/> merges the in-memory list OVER the gateway
///    Postgres mirror — delivery-service has no client-scoped list, so the durable
///    owner-list is served from <c>delivery_requests</c> (create mirrors each row
///    idempotently; a committed cancel updates its status).
/// The mirror is OPTIONAL (registered only with <c>GatewayPostgres:ConnectionString</c>);
/// absent, the reads degrade to the in-memory model — exactly today's behaviour.
///
/// FLAG-GATED: registered ahead of the in-memory store only when
/// <c>FeatureFlags:DurableRequests:Enabled=true</c>. Default OFF keeps today's
/// green path (S05 H3 → 201, S01–S04) byte-for-byte unchanged.
/// </summary>
public sealed class DurableRequestsStore : IRequestsStore
{
    private readonly IRequestsStore _inner;
    private readonly IDeliveryServiceClient _delivery;
    private readonly ISagaBundleRecorder _bundles;
    private readonly IConversationProvisioner _conversations;
    private readonly IBroadcastEventRecorder _broadcasts;
    private readonly DurableRequestsOptions _options;
    private readonly ILogger<DurableRequestsStore> _logger;

    /// <summary>
    /// requests-durable: OPTIONAL gateway-Postgres owner-list mirror. Registered
    /// only inside the <c>GatewayPostgres:ConnectionString</c> block, so it is
    /// <see langword="null"/> when Postgres is not configured — in which case the
    /// durable owner-list gracefully degrades to the in-memory snapshot (today's
    /// behaviour) and the create/cancel mirror side-effects are skipped.
    /// </summary>
    private readonly IDurableRequestsMirror? _mirror;

    public DurableRequestsStore(
        IRequestsStore inner,
        IDeliveryServiceClient delivery,
        ISagaBundleRecorder bundles,
        IConversationProvisioner conversations,
        IBroadcastEventRecorder broadcasts,
        IOptions<DurableRequestsOptions> options,
        ILogger<DurableRequestsStore> logger,
        IDurableRequestsMirror? mirror = null)
    {
        _inner = inner;
        _delivery = delivery;
        _bundles = bundles;
        _conversations = conversations;
        _broadcasts = broadcasts;
        _options = options.Value;
        _logger = logger;
        _mirror = mirror;
    }

    // -----------------------------------------------------------------------
    // CREATE — the only durable-aware path. Everything else delegates verbatim.
    // -----------------------------------------------------------------------

    public async Task<DeliveryRequest> TryCreateWithLimitAsync(
        CreateRequestInput input,
        int limit,
        CancellationToken ct)
    {
        // 1) Mint ONE stable id up front (honour a caller-supplied id — the S04
        //    voice-create idempotency anchor — so durable + voice paths agree).
        var stableId = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id;
        var stamped = WithId(input, stableId);

        // 2) Enforce BR-9 atomically in the inner store FIRST. A cap rejection
        //    throws here, BEFORE any upstream side-effect — so a rejected create
        //    never leaves an orphan delivery row or saga bundle.
        var created = await _inner.TryCreateWithLimitAsync(stamped, limit, ct);

        // 3) Seed the durable delivery row + record the saga. Same id throughout.
        await PersistSagaAsync(created, ct);
        return created;
    }

    public async Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct)
    {
        var stableId = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id;
        var stamped = WithId(input, stableId);

        var created = await _inner.CreateAsync(stamped, ct);
        await PersistSagaAsync(created, ct);
        return created;
    }

    /// <summary>
    /// Seeds the canonical delivery row and records the saga bundle for a
    /// freshly created request. The delivery row is a HARD dependency (it is the
    /// matching-resolve source of truth — the exact thing ADR-006 fixes), so a
    /// create-row failure surfaces. The bundle is the durable audit/saga trail:
    /// the recorder degrades to <see cref="SagaBundleRecordOutcome.Unavailable"/>
    /// on a state-service blip rather than failing the user's create.
    /// </summary>
    private async Task PersistSagaAsync(DeliveryRequest created, CancellationToken ct)
    {
        // Delivery row needs pickup coordinates to back the matching resolve.
        // A scheduled/immediate row always carries a validated pickup at the
        // controller; guard defensively so a malformed input degrades to the
        // in-memory-only behaviour instead of throwing a NRE.
        if (created.PickupLocation is null || string.IsNullOrWhiteSpace(created.TierId))
        {
            _logger.LogWarning(
                "Durable create for {RequestId} missing pickup/tier; recorded in-memory only.",
                created.Id);
            return;
        }

        // (a) Seed the delivery row — gateway forwards the SAME id as the row id.
        var row = await _delivery.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
        {
            Id = created.Id,
            TenantId = _options.TenantId,
            ClientId = created.ClientId,
            TierId = created.TierId,
            PickupLat = created.PickupLocation.Lat,
            PickupLng = created.PickupLocation.Lng,
        }, ct);

        // Stability assertion: the seeded row id MUST equal the request id, or
        // the later matching run silently re-introduces the 404.
        if (!string.Equals(row.Id, created.Id, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Durable create id mismatch: request {RequestId} but delivery row {RowId}.",
                created.Id, row.Id);
            throw new InvalidOperationException(
                $"Delivery row id ({row.Id}) does not match request id ({created.Id}); matching/run would 404.");
        }

        // (b) JEB-50 (S05 H7): auto-create the broadcasting conversation and
        // stamp its id onto the already-inserted row so the create DTO and the
        // read-back (GET /requests/{id}) surface conversationId, which H9b reads
        // via GET /api/Chat/channels/{conversationId}/summary. Thin orchestration
        // only — the provisioner composes the chat-service typed client and owns
        // no conversation state. SECONDARY side-effect of create: the provisioner
        // degrades to null on a chat blip (or when the flag is OFF) so a chat
        // outage never fails the order create. Stamping a mutable property on the
        // row reference already held in the inner store needs no re-insert and
        // does not affect the BR-9 cap (the row is already counted).
        var conversationId = await _conversations.CreateBroadcastingConversationAsync(
            created.Id, created.ClientId, ct);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            created.ConversationId = conversationId;

            // (b.1) JEB-50 (S05 H9b): the order has entered the broadcasting
            // phase (a broadcasting-tagged conversation now exists in chat). Per
            // the OWNER DIRECTIVE, LOG that broadcast event to the jeeb-state
            // bundler so it is durable and visible cross-service. This is fired
            // ONLY when the provisioner returned a real conversation id — i.e.
            // only when ConversationAutoCreate is ON and chat actually created the
            // broadcasting channel — so it is implicitly flag-gated by the same
            // switch that produces the conversation. Thin orchestration: the
            // gateway composes the chat-create + the state-log; it holds no
            // broadcast/phase state of its own (chat owns the phase, state-service
            // owns the durable log). DEGRADE-DON'T-FAIL: a state-service blip
            // returns Unavailable and the order create still succeeds — mirroring
            // the saga-bundle recorder. The broadcast log is the audit trail, not
            // the matching-resolve hard dependency.
            await _broadcasts.RecordBroadcastingAsync(
                contextId: conversationId,
                phase: _options.BroadcastingPhase,
                ct);
        }

        // (c) Record the saga — opaque state keyed by the same id as sourceId.
        await _bundles.RecordCreatedAsync(
            sourceId: created.Id,
            tag: _options.SagaTag,
            state: new
            {
                status = "created",
                request_id = created.Id,
                client_id = created.ClientId,
                tier_id = created.TierId,
                conversation_id = created.ConversationId,
                scheduled = created.ScheduledAt is not null,
                created_at = created.CreatedAt,
            },
            ct);

        // (d) requests-durable: mirror the row into the gateway Postgres
        // delivery_requests table so the owner-list (ListForClientAsync) survives
        // a gateway bounce. delivery-service exposes no client-scoped list, so the
        // gateway serves that read from its own Postgres. BEST-EFFORT: a mirror
        // fault NEVER fails the create — the BR-9 cap + the delivery-row seed (the
        // matching-resolve hard dependency) are already committed above, so this
        // is a secondary durability side-effect, exactly like the saga bundle.
        await MirrorCreateAsync(created, ct);
    }

    /// <summary>
    /// requests-durable: idempotently mirrors a created request into the durable
    /// owner-list store. No-op when no mirror is wired (Postgres absent) or the
    /// row lacks a pickup/dropoff point (the mirror's geography columns are NOT
    /// NULL). Never throws into the create path.
    /// </summary>
    private async Task MirrorCreateAsync(DeliveryRequest created, CancellationToken ct)
    {
        if (_mirror is null) return;
        if (created.PickupLocation is null || created.DropoffLocation is null) return;

        try
        {
            await _mirror.UpsertOnCreateAsync(created, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "requests-durable: owner-list mirror upsert failed for {RequestId}; the row is durable in " +
                "delivery-service but absent from the gateway owner-list mirror until the next write.",
                created.Id);
        }
    }

    private static CreateRequestInput WithId(CreateRequestInput input, string id) => new()
    {
        ClientId = input.ClientId,
        Description = input.Description,
        Id = id,
        Transcription = input.Transcription,
        TranscriptionConfidence = input.TranscriptionConfidence,
        AudioUrl = input.AudioUrl,
        Photos = input.Photos,
        TierId = input.TierId,
        PickupLocation = input.PickupLocation,
        DropoffLocation = input.DropoffLocation,
        PickupAddress = input.PickupAddress,
        DropoffAddress = input.DropoffAddress,
        RecipientPhone = input.RecipientPhone,
        ScheduledAt = input.ScheduledAt,
    };

    // -----------------------------------------------------------------------
    // The remaining methods delegate verbatim to the inner store, EXCEPT the
    // requests-durable read/cancel trio (GetAsync, ListForClientAsync,
    // TryCancelAsync — below) which are durability-aware. The other lifecycle
    // transitions/sweepers are not yet relocated; the gateway keeps the
    // in-memory model for them so this stays surgical.
    // -----------------------------------------------------------------------

    public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct)
        => _inner.CountActiveForClientAsync(clientId, ct);

    /// <summary>
    /// F4: status mutation with a durable EFFECT. The in-memory model owns the
    /// authoritative state machine (delegated verbatim); on a committed change the
    /// new status is reflected onto the durable owner-list mirror (best-effort) so a
    /// post-bounce <see cref="ListForClientAsync"/> shows the live status instead of
    /// the stale create-time <c>pending</c>.
    /// </summary>
    public async Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
    {
        var ok = await _inner.SetStatusAsync(requestId, status, ct);
        if (ok)
        {
            await MirrorLifecycleAsync(requestId, gwStatus: status, gwJeeberId: null, gwAcceptedFee: null, ct);
        }
        return ok;
    }

    /// <summary>F4: jeeber assignment mirrored to the durable owner-list (best-effort).</summary>
    public async Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct)
    {
        var ok = await _inner.SetJeeberIdAsync(requestId, jeeberId, ct);
        if (ok)
        {
            await MirrorLifecycleAsync(requestId, gwStatus: null, gwJeeberId: jeeberId, gwAcceptedFee: null, ct);
        }
        return ok;
    }

    /// <summary>F4: accepted-fee mirrored to the durable owner-list (best-effort).</summary>
    public async Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct)
    {
        var ok = await _inner.TrySetAcceptedFeeAsync(requestId, fee, ct);
        if (ok)
        {
            await MirrorLifecycleAsync(requestId, gwStatus: null, gwJeeberId: null, gwAcceptedFee: fee, ct);
        }
        return ok;
    }

    /// <summary>
    /// F4: best-effort reflection of an owner-list-visible lifecycle mutation onto the
    /// durable mirror. No-op when no mirror is wired (Postgres absent). NEVER throws
    /// into a mutation path — a mirror fault only means the owner-list may show a
    /// stale field until the next write (exactly the pre-fix behaviour).
    /// </summary>
    private async Task MirrorLifecycleAsync(
        string requestId, string? gwStatus, string? gwJeeberId, decimal? gwAcceptedFee, CancellationToken ct)
    {
        if (_mirror is null) return;
        try
        {
            await _mirror.UpdateLifecycleAsync(
                requestId, gwStatus, gwJeeberId, gwAcceptedFee, DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "requests-durable: owner-list mirror lifecycle update failed for {RequestId}; " +
                "the list may show a stale status/jeeber/fee after a bounce.",
                requestId);
        }
    }

    public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
        => _inner.GetByConversationIdAsync(conversationId, ct);

    /// <summary>
    /// FT-08: durable expiry-sweep read. When the durable path is active the
    /// canonical delivery-service row (seeded on create) is the source of truth
    /// for Ordered/pending deliveries. This override queries delivery-service for
    /// Ordered shipments and merges them with the in-memory mirror so the sweep
    /// survives a gateway restart (the inner store is empty after a restart, but
    /// the canonical rows live in delivery-service).
    /// </summary>
    public async Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        IReadOnlyList<DeliveryRequest> innerCandidates;
        try
        {
            innerCandidates = await _inner.ListPendingCreatedAtOrBeforeAsync(cutoff, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FT-08: inner-store sweep read failed; falling back to delivery-service only");
            innerCandidates = Array.Empty<DeliveryRequest>();
        }

        // Fetch Ordered shipments from delivery-service and overlay any that
        // are not yet in the in-memory mirror (post-restart scenario).
        List<DeliveryRequest> merged = new(innerCandidates);
        var knownIds = new HashSet<string>(innerCandidates.Select(r => r.Id), StringComparer.Ordinal);

        try
        {
            var shipments = await _delivery.ListShipmentsAsync(orderId: null, stage: "Ordered", limit: null, ct);
            foreach (var s in shipments.Shipments)
            {
                if (s.CreatedAt > cutoff) continue;
                if (knownIds.Contains(s.Id)) continue;

                // Row exists in delivery-service but not in local mirror (post-restart).
                // Build a minimal DeliveryRequest from the canonical data so the sweeper
                // can expire it. Fields not available from the shipment are left empty —
                // TryExpireAsync only needs the Id and the sweeper only checks Status/CreatedAt.
                merged.Add(new DeliveryRequest
                {
                    Id          = s.Id,
                    ClientId    = string.Empty,
                    Status      = RequestStatus.Pending,
                    Description = string.Empty,
                    CreatedAt   = s.CreatedAt,
                });
            }
        }
        catch (Exception ex)
        {
            // Delivery-service unreachable: log and return in-memory results.
            // The sweep will process whatever the mirror has; rows that only
            // live in delivery-service will be swept on the next cycle once
            // the service is reachable again.
            _logger.LogWarning(ex,
                "FT-08: delivery-service sweep read failed; using in-memory mirror only " +
                "({Count} candidates). Rows that survived a restart may miss this sweep cycle.",
                innerCandidates.Count);
        }

        return merged;
    }

    public Task<bool> MarkNudgedAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _inner.MarkNudgedAsync(requestId, at, ct);

    public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _inner.TryExpireAsync(requestId, at, ct);

    public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct)
        => _inner.AnonymizeForClientAsync(userId, anonymizedHash, ct);

    public Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(DateTimeOffset cutoff, CancellationToken ct)
        => _inner.ListScheduledDueAsync(cutoff, ct);

    public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _inner.TryActivateScheduledAsync(requestId, at, ct);

    /// <summary>
    /// requests-durable: durable single read. The fast in-memory mirror is
    /// authoritative when warm — byte-for-byte identical to today, with every
    /// gateway-only field (ConversationId / AcceptedFee / OTP state / …) intact.
    /// On a MISS (gateway bounce / cross-replica read) the row is resolved
    /// through delivery-service — the canonical owner — mapping
    /// <see cref="DeliveryRequestUpstream"/> → <see cref="DeliveryRequest"/> so
    /// the read survives a restart. DEGRADE-DON'T-FAIL: a 404 / transport fault
    /// yields <see langword="null"/> — exactly today's unknown-id behaviour
    /// (controllers map null → 404) — so this is a STRICT SUPERSET, never a new
    /// failure mode on the hot path.
    /// </summary>
    public async Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
    {
        var local = await _inner.GetAsync(requestId, ct);
        if (local is not null)
        {
            return local;
        }

        try
        {
            var upstream = await _delivery.GetDeliveryAsync(requestId, ct);
            return MapUpstream(upstream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // GetDeliveryAsync throws on 404 (EnsureSuccessStatusCode) and on any
            // transport fault; a miss/blip is an unknown-id → null (== today).
            _logger.LogDebug(ex,
                "requests-durable: delivery-service read-through miss for {RequestId}; returning null (unknown-id).",
                requestId);
            return null;
        }
    }

    /// <summary>
    /// requests-durable: durable owner-list. delivery-service exposes NO
    /// client-scoped list, so the durable rows come from the gateway Postgres
    /// mirror (seeded on create). The in-memory list is MERGED OVER the mirror:
    /// an in-memory row wins on id collision (it carries the live status + every
    /// gateway-only field), while the mirror contributes rows the in-memory model
    /// lost on a bounce. Oldest-first, matching the in-memory snapshot ordering.
    /// STRICT SUPERSET: with no mirror wired (Postgres absent) this is exactly the
    /// in-memory list; with a mirror it additionally survives a restart.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
    {
        var localRows = await _inner.ListForClientAsync(clientId, ct);
        if (_mirror is null)
        {
            return localRows;
        }

        IReadOnlyList<DeliveryRequest> durableRows;
        try
        {
            durableRows = await _mirror.ListForClientAsync(clientId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "requests-durable: owner-list mirror read failed for {ClientId}; returning in-memory rows only.",
                clientId);
            return localRows;
        }

        if (durableRows.Count == 0)
        {
            return localRows;
        }

        // Merge by id; the in-memory row wins (live status + full field set).
        var byId = new Dictionary<string, DeliveryRequest>(StringComparer.Ordinal);
        foreach (var row in durableRows)
        {
            byId[row.Id] = row;
        }
        foreach (var row in localRows)
        {
            byId[row.Id] = row;
        }
        return byId.Values.OrderBy(r => r.CreatedAt).ToArray();
    }

    /// <summary>
    /// Maps a canonical delivery-service row (the gateway-vocab
    /// <c>jeeb/deliveries/{id}</c> projection) onto a <see cref="DeliveryRequest"/>
    /// for the durable read-through. Fields delivery-service does not carry
    /// (ScheduledAt / ConversationId / AcceptedFee / DeliveryOtp / …) are left at
    /// their defaults — acceptable on the cold path, which was an unresolvable
    /// null before this read-through existed.
    /// </summary>
    private static DeliveryRequest MapUpstream(DeliveryRequestUpstream u) => new()
    {
        Id = u.Id,
        ClientId = u.ClientId,
        Status = u.Status,
        Description = u.Description ?? string.Empty,
        AudioUrl = u.AudioUrl,
        Photos = u.Photos,
        TierId = u.TierId,
        PickupLocation = u.Pickup is null ? null : new GeoPoint { Lat = u.Pickup.Lat, Lng = u.Pickup.Lng },
        DropoffLocation = u.Dropoff is null ? null : new GeoPoint { Lat = u.Dropoff.Lat, Lng = u.Dropoff.Lng },
        PickupAddress = u.PickupAddress,
        DropoffAddress = u.DropoffAddress,
        RecipientPhone = u.RecipientPhone,
        CreatedAt = u.CreatedAt,
        JeeberId = u.JeeberId,
        AcceptedAt = u.AcceptedAt,
        GpsTrackingActive = u.GpsTrackingActive,
        OtpAttemptCount = u.OtpAttemptCount,
        OtpLockedAt = u.OtpLockedAt,
        OtpEscalationId = u.OtpEscalationId,
        CancelledBy = u.CancelledBy,
        CancellationReason = u.CancellationReason,
    };

    /// <summary>
    /// JEBV4-140: durable jeeber-side owner-list, symmetric with
    /// <see cref="ListForClientAsync"/>. delivery-service exposes NO jeeber-scoped
    /// list, so — exactly like the client side — the durable rows come from the
    /// gateway Postgres mirror (seeded on create, jeeber assigned post-accept). The
    /// in-memory list is MERGED OVER the mirror: an in-memory row wins on id
    /// collision (it carries the live status + every gateway-only field), while the
    /// mirror contributes rows the in-memory model lost on a bounce. Newest-first,
    /// matching the in-memory jeeber-list ordering (DESCENDING — note this differs
    /// from the client list's oldest-first). STRICT SUPERSET: with no mirror wired
    /// (Postgres absent) this is exactly the in-memory list; with a mirror it
    /// additionally survives a restart — which is the whole point (a jeeber's
    /// accepted deliveries previously vanished on a process bounce while the client
    /// side survived).
    ///
    /// The stateless end-state is to read jeeber deliveries from delivery-service
    /// directly (JEBV4-140 follow-up); the mirror-read is the correct minimal fix now.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
    {
        var localRows = await _inner.ListForJeeberAsync(jeeberId, ct);
        if (_mirror is null)
        {
            return localRows;
        }

        IReadOnlyList<DeliveryRequest> durableRows;
        try
        {
            durableRows = await _mirror.ListForJeeberAsync(jeeberId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "requests-durable: jeeber-list mirror read failed for {JeeberId}; returning in-memory rows only.",
                jeeberId);
            return localRows;
        }

        if (durableRows.Count == 0)
        {
            return localRows;
        }

        // Merge by id; the in-memory row wins (live status + full field set).
        // Newest-first to match the in-memory jeeber-list ordering.
        var byId = new Dictionary<string, DeliveryRequest>(StringComparer.Ordinal);
        foreach (var row in durableRows)
        {
            byId[row.Id] = row;
        }
        foreach (var row in localRows)
        {
            byId[row.Id] = row;
        }
        return byId.Values.OrderByDescending(r => r.CreatedAt).ToArray();
    }

    public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct)
        => _inner.CountActiveForJeeberAsync(jeeberId, ct);

    /// <summary>
    /// F4: jeeber-accept with a durable EFFECT. The in-memory model owns the
    /// authoritative accept state machine (delegated verbatim); on a committed accept
    /// the resulting status + assigned jeeber are reflected onto the durable owner-list
    /// mirror (best-effort) so a post-bounce list shows the accepted/in-progress state
    /// and the jeeberId instead of the stale create-time <c>pending</c>.
    /// </summary>
    public async Task<DeliveryRequest?> TryAcceptByJeeberAsync(string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct)
    {
        var result = await _inner.TryAcceptByJeeberAsync(requestId, jeeberId, limit, at, ct);
        if (result is not null)
        {
            await MirrorLifecycleAsync(
                requestId,
                gwStatus: result.Status,
                gwJeeberId: string.IsNullOrWhiteSpace(result.JeeberId) ? jeeberId : result.JeeberId,
                gwAcceptedFee: result.AcceptedFee,
                ct);
        }
        return result;
    }

    /// <summary>
    /// requests-durable: cancel with a durable EFFECT. The in-memory model owns
    /// the authoritative cancel state machine (the admin-approval queue →
    /// <c>cancellation_requested</c>, the <c>PreviousStatus</c> revert, and the
    /// under-lock re-check), so the mutation is delegated VERBATIM — every cancel
    /// semantic is preserved exactly. On a committed cancel the resulting status
    /// is reflected onto the durable owner-list mirror (best-effort) so a
    /// post-bounce list shows the cancel.
    ///
    /// <para>The cancel MUTATION is deliberately NOT proxied to delivery-service
    /// <c>CancelDeliveryAsync</c>: the store-level signature carries NO acting
    /// user id (<paramref name="cancelledBy"/> is the ROLE literal
    /// <c>client</c>/<c>jeeber</c>, and the upstream cancel body REQUIRES a
    /// UserId), and delivery-service has no <c>cancellation_requested</c>
    /// admin-queue state — so a proxy would drop the admin-approval semantics and
    /// violate the strict-superset guarantee. The durable cancel is therefore the
    /// gateway-Postgres mirror update, not an upstream call.</para>
    /// </summary>
    public async Task<CancellationStoreResult?> TryCancelAsync(
        string requestId,
        IReadOnlySet<string> allowedFromStates,
        string targetStatus,
        string cancelledBy,
        string? reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var result = await _inner.TryCancelAsync(
            requestId, allowedFromStates, targetStatus, cancelledBy, reason, at, ct);

        if (_mirror is not null && result is { Outcome: CancellationStoreOutcome.Committed })
        {
            try
            {
                await _mirror.MarkCancelledAsync(
                    requestId, result.Request.Status, cancelledBy, reason, at, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "requests-durable: owner-list mirror cancel-status update failed for {RequestId}; " +
                    "the list may show a stale status after a bounce.",
                    requestId);
            }
        }

        return result;
    }

    public Task<CancellationStoreResult?> TryDecideCancellationAsync(string requestId, bool approve, DateTimeOffset at, CancellationToken ct)
        => _inner.TryDecideCancellationAsync(requestId, approve, at, ct);

    public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(int page, int pageSize, CancellationToken ct)
        => _inner.ListPendingCancellationsAsync(page, pageSize, ct);

    public Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(string jeeberId, CancellationToken ct)
        => _inner.ListJeeberCancelledAsync(jeeberId, ct);

    public Task<OtpVerificationResult> TryVerifyOtpAsync(string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct)
        => _inner.TryVerifyOtpAsync(requestId, otpCode, maxAttempts, at, ct);

    public Task<DeliveryRequest?> MarkClientUnreachableAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _inner.MarkClientUnreachableAsync(requestId, at, ct);

    public Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
        => _inner.ListUnreachableAtOrBeforeAsync(cutoff, ct);

    public Task<bool> TrySetEscalationIdAsync(string requestId, string escalationId, CancellationToken ct)
        => _inner.TrySetEscalationIdAsync(requestId, escalationId, ct);
}
