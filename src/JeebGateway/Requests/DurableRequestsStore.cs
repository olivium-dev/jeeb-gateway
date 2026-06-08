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
/// THIN ORCHESTRATION ONLY: this decorator composes existing typed clients. It
/// holds no domain logic of its own (no DB connection, no status machine) — the
/// in-memory inner store remains the gateway's fast read/query/sweeper model
/// for the not-yet-relocated lifecycle reads, and EVERY non-create method
/// delegates to it verbatim. The delivery row is the matching-resolve source of
/// truth; the bundle is the durable saga/audit trail.
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
    private readonly DurableRequestsOptions _options;
    private readonly ILogger<DurableRequestsStore> _logger;

    public DurableRequestsStore(
        IRequestsStore inner,
        IDeliveryServiceClient delivery,
        ISagaBundleRecorder bundles,
        IOptions<DurableRequestsOptions> options,
        ILogger<DurableRequestsStore> logger)
    {
        _inner = inner;
        _delivery = delivery;
        _bundles = bundles;
        _options = options.Value;
        _logger = logger;
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

        // (b) Record the saga — opaque state keyed by the same id as sourceId.
        await _bundles.RecordCreatedAsync(
            sourceId: created.Id,
            tag: _options.SagaTag,
            state: new
            {
                status = "created",
                request_id = created.Id,
                client_id = created.ClientId,
                tier_id = created.TierId,
                scheduled = created.ScheduledAt is not null,
                created_at = created.CreatedAt,
            },
            ct);
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
    // Everything below delegates verbatim to the inner store. The lifecycle
    // reads/transitions/sweepers are not yet relocated; the gateway keeps the
    // in-memory model for them so this PR stays surgical (create-only durable).
    // -----------------------------------------------------------------------

    public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct)
        => _inner.CountActiveForClientAsync(clientId, ct);

    public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
        => _inner.SetStatusAsync(requestId, status, ct);

    public Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
        => _inner.ListPendingCreatedAtOrBeforeAsync(cutoff, ct);

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

    public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
        => _inner.GetAsync(requestId, ct);

    public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
        => _inner.ListForClientAsync(clientId, ct);

    public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct)
        => _inner.CountActiveForJeeberAsync(jeeberId, ct);

    public Task<DeliveryRequest?> TryAcceptByJeeberAsync(string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct)
        => _inner.TryAcceptByJeeberAsync(requestId, jeeberId, limit, at, ct);

    public Task<DeliveryTransitionResult> TryTransitionAsync(string requestId, string toStatus, string? otp, CancellationToken ct)
        => _inner.TryTransitionAsync(requestId, toStatus, otp, ct);

    public Task<CancellationStoreResult?> TryCancelAsync(string requestId, IReadOnlySet<string> allowedFromStates, string targetStatus, string cancelledBy, string? reason, DateTimeOffset at, CancellationToken ct)
        => _inner.TryCancelAsync(requestId, allowedFromStates, targetStatus, cancelledBy, reason, at, ct);

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
