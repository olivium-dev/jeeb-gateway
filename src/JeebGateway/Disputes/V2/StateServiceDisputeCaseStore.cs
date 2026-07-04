using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// Durable <see cref="IDisputeCaseStore"/> backed by <b>jeeb-state-service</b> (ADR-0001:
/// the gateway is STATELESS &amp; THIN — it must hold no dispute-case row in process memory).
/// Replaces <see cref="InMemoryDisputeCaseStore"/>, whose rows evaporated on every gateway
/// bounce / replica move — the same ADR-0001 gap already remediated for the legacy v1 dispute
/// row by <see cref="JeebGateway.Disputes.StateServiceDisputeStore"/>. This store is a near
/// 1:1 mirror of that implementation, extended with the two idempotency-key indexes T-BE-028
/// (JEB-64) needs for replay-safe escalate + resolve (PO review blocker #6).
///
/// <para><b>Why the opaque KV (not a typed state-service primitive).</b> Same rationale as
/// <see cref="JeebGateway.Disputes.StateServiceDisputeStore"/>: the state-service's generic
/// dispute envelope does not carry the Jeeb-specific fields (reason, evidence bundle, refund
/// ledger id, …) the v2 case surface needs. The full <see cref="DisputeCase"/> row is
/// persisted as one OPAQUE JSON body in the state-service idempotency KV — the same
/// key→opaque-body store with GET-by-key + prefix-scan that the legacy dispute store, the
/// support-ticket store (PR #206) and the durable offer-request index all reuse. The gateway
/// holds NO row itself; everything survives a bounce.</para>
///
/// <para><b>Mutable state on an insert-once KV.</b> The KV is exactly-once
/// (<c>INSERT … ON CONFLICT (key) DO NOTHING</c>) — a key cannot be overwritten. A case's
/// state (and its evidence bundle / resolution fields) mutate over its lifetime
/// (open → under_review → resolved_*), so every mutation — <see cref="ApplyUnderReviewAsync"/>,
/// <see cref="ReplaceEvidenceAsync"/> and <see cref="ApplyResolutionAsync"/> alike — is
/// modelled as an APPEND onto a single, shared, append-only chain of version keys
/// <c>dispute-case-status:{id}:{seq}</c>, each carrying the FULL row snapshot at that
/// revision. The immutable base row lives at <c>dispute-case:{id}</c> (seq 0). A read
/// resolves the row by walking the chain to the highest existing seq — the same monotone-write
/// pattern the legacy dispute store uses to express mutation, generalised here to cover all
/// three mutation shapes instead of just a single state transition.</para>
///
/// <para><b>Indexes.</b> Four secondary-index families are written alongside the row so every
/// non-by-id / non-by-idempotency-key read is a prefix scan (no full-table scan):
/// <list type="bullet">
///   <item><c>dispute-case-delivery:{deliveryId}:{id}</c> → <see cref="GetActiveForDeliveryAsync"/>
///     (prefix scan + terminal-state filter; "active" = not <see cref="DisputeCaseState.IsResolved"/>).</item>
///   <item><c>dispute-case-user:{userId}:{id}</c> → <see cref="ListForUserAsync"/>. Written for
///     BOTH the opener and the counterparty (when present and distinct) so a user sees cases
///     they filed AND cases filed against them — the same two-role scope
///     <see cref="InMemoryDisputeCaseStore"/> applies today (behaviour-preservation).</item>
///   <item><c>dispute-case-idem:{idempotencyKey}</c> → case id, read by
///     <see cref="GetByIdempotencyKeyAsync"/> (escalate replay, PO blocker #6).</item>
///   <item><c>dispute-case-resolve-idem:{resolveIdempotencyKey}</c> → case id (resolve replay,
///     PO blocker #6). Not read back through this interface today —
///     <c>DisputeCaseService.ResolveAsync</c> detects a resolve replay off the row's own
///     <see cref="DisputeCase.ResolveIdempotencyKey"/> field via <see cref="GetByIdAsync"/> —
///     but the index is maintained here for parity with the escalate side and for future
///     direct lookups (admin tooling / migration path).</item>
/// </list>
/// Index rows are written ONCE at file time (immutable id↔delivery / id↔user / id↔idempotency-key
/// edges); the live state is always re-read from the status chain so a stale index value never
/// masks a transition.</para>
///
/// <para><b>TTL.</b> 180 days — matches the legacy v1 dispute store; longer than a case's
/// active-review window while bounding KV growth.</para>
/// </summary>
public sealed class StateServiceDisputeCaseStore : IDisputeCaseStore
{
    internal const string RowKeyPrefix = "dispute-case:";
    internal const string StatusKeyPrefix = "dispute-case-status:";
    internal const string DeliveryKeyPrefix = "dispute-case-delivery:";
    internal const string UserKeyPrefix = "dispute-case-user:";
    internal const string IdemKeyPrefix = "dispute-case-idem:";
    internal const string ResolveIdemKeyPrefix = "dispute-case-resolve-idem:";

    /// <summary>180-day TTL (seconds).</summary>
    internal const int TtlSeconds = 180 * 24 * 60 * 60;

    /// <summary>Defensive upper bound on the per-case status chain length.</summary>
    private const int MaxStatusRevisions = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _kv;
    private readonly ILogger<StateServiceDisputeCaseStore> _logger;

    public StateServiceDisputeCaseStore(IIdempotencyStore kv, ILogger<StateServiceDisputeCaseStore> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    public async Task<DisputeCase> AddAsync(DisputeCase @case, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@case);

        var body = Serialize(@case);

        // Base (seq 0) immutable snapshot — the by-id anchor.
        await _kv.PutOrGetAsync(RowKeyPrefix + @case.Id, statusCode: 201, body, TtlSeconds, ct);

        // Delivery + user indexes (immutable edges) so GetActiveForDelivery / ListForUser are
        // prefix scans, not full scans. The user index is written for both roles so a
        // counterparty also sees the case (matches InMemoryDisputeCaseStore.ListForUserAsync).
        if (!string.IsNullOrWhiteSpace(@case.DeliveryId))
            await _kv.PutOrGetAsync(
                DeliveryKeyPrefix + @case.DeliveryId + ":" + @case.Id, 201, @case.Id, TtlSeconds, ct);

        if (!string.IsNullOrWhiteSpace(@case.OpenedByUserId))
            await _kv.PutOrGetAsync(
                UserKeyPrefix + @case.OpenedByUserId + ":" + @case.Id, 201, @case.Id, TtlSeconds, ct);

        if (!string.IsNullOrWhiteSpace(@case.CounterpartyUserId)
            && !string.Equals(@case.CounterpartyUserId, @case.OpenedByUserId, StringComparison.Ordinal))
            await _kv.PutOrGetAsync(
                UserKeyPrefix + @case.CounterpartyUserId + ":" + @case.Id, 201, @case.Id, TtlSeconds, ct);

        // Escalate idempotency-key index (PO blocker #6 replay safety).
        if (!string.IsNullOrWhiteSpace(@case.IdempotencyKey))
            await _kv.PutOrGetAsync(
                IdemKeyPrefix + @case.IdempotencyKey, 201, @case.Id, TtlSeconds, ct);

        return @case;
    }

    public async Task<DisputeCase?> GetByIdAsync(string caseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseId)) return null;
        return await ResolveLatestAsync(caseId.Trim(), ct);
    }

    public async Task<DisputeCase?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;

        var outcome = await _kv.GetAsync(IdemKeyPrefix + idempotencyKey, ct);
        var caseId = outcome?.ResponseBodyJson?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(caseId)) return null;

        return await ResolveLatestAsync(caseId, ct);
    }

    public async Task<DisputeCase?> GetActiveForDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return null;

        var ids = await ListIdsByPrefixAsync(DeliveryKeyPrefix + deliveryId + ":", ct);
        DisputeCase? active = null;
        foreach (var id in ids)
        {
            var row = await ResolveLatestAsync(id, ct);
            if (row is null || DisputeCaseState.IsResolved(row.State)) continue;
            if (active is null || row.OpenedAt > active.OpenedAt) active = row;
        }
        return active;
    }

    public async Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<DisputeCase>();

        var ids = await ListIdsByPrefixAsync(UserKeyPrefix + userId + ":", ct);
        var rows = new List<DisputeCase>(ids.Count);
        foreach (var id in ids)
        {
            var row = await ResolveLatestAsync(id, ct);
            if (row is not null) rows.Add(row);
        }
        return rows
            .OrderByDescending(c => c.OpenedAt)
            .ToList();
    }

    public async Task<DisputeCase?> ApplyResolutionAsync(string caseId, DisputeCaseResolutionPatch patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseId)) return null;
        ArgumentNullException.ThrowIfNull(patch);

        var (latest, seq) = await ResolveLatestWithSeqAsync(caseId.Trim(), ct);
        if (latest is null) return null;

        // Apply the patch onto the latest snapshot — Id/DeliveryId/OpenedByUserId/Reason/…
        // are immutable; only the resolution fields change.
        latest.State = patch.State;
        latest.ResolvedAt = patch.ResolvedAt;
        latest.ResolverAdminId = patch.ResolverAdminId;
        latest.ResolutionNotes = patch.ResolutionNotes;
        latest.RefundUsd = patch.RefundUsd;
        latest.RefundLedgerEntryId = patch.RefundLedgerEntryId;
        latest.ResolveIdempotencyKey = patch.ResolveIdempotencyKey;

        // Append the new revision. Insert-once on the next seq key gives a natural
        // optimistic-concurrency guard: if a concurrent writer already took seq+1 the
        // PutOrGet returns Inserted=false and we surface a conflict by re-resolving.
        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.Id + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);

        DisputeCase? result;
        if (!outcome.Inserted)
        {
            // Lost the race for this seq — re-resolve and return the authoritative row.
            _logger.LogWarning(
                "dispute-case {CaseId} concurrent resolution at seq {Seq}; returning authoritative latest", latest.Id, nextSeq);
            result = await ResolveLatestAsync(latest.Id, ct);
        }
        else
        {
            result = latest;
        }

        // Resolve idempotency-key index (PO blocker #6). Insert-once and written regardless
        // of which concurrent writer's seq landed — both writers are resolving the SAME
        // caseId, so the key→id mapping is correct either way.
        if (!string.IsNullOrWhiteSpace(patch.ResolveIdempotencyKey))
            await _kv.PutOrGetAsync(
                ResolveIdemKeyPrefix + patch.ResolveIdempotencyKey, 201, latest.Id, TtlSeconds, ct);

        return result;
    }

    public async Task<DisputeCase?> ReplaceEvidenceAsync(string caseId, DisputeEvidence evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseId)) return null;
        ArgumentNullException.ThrowIfNull(evidence);

        var (latest, seq) = await ResolveLatestWithSeqAsync(caseId.Trim(), ct);
        if (latest is null) return null;

        latest.Evidence = evidence;

        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.Id + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);

        if (!outcome.Inserted)
        {
            _logger.LogWarning(
                "dispute-case {CaseId} concurrent evidence replace at seq {Seq}; returning authoritative latest", latest.Id, nextSeq);
            return await ResolveLatestAsync(latest.Id, ct);
        }

        return latest;
    }

    public async Task<DisputeCase?> ApplyUnderReviewAsync(string caseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseId)) return null;

        var (latest, seq) = await ResolveLatestWithSeqAsync(caseId.Trim(), ct);
        if (latest is null) return null;

        // The CanTransition guard runs against the freshly-resolved latest snapshot so a
        // racing resolve that already landed (higher seq) is observed here before we attempt
        // to append — the append-only-chain analogue of the in-memory store's lock-guarded
        // check.
        if (!DisputeCaseState.CanTransition(latest.State, DisputeCaseState.UnderReview))
        {
            return null;
        }

        latest.State = DisputeCaseState.UnderReview;

        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.Id + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);

        if (!outcome.Inserted)
        {
            // Lost the race for this seq to a concurrent mutation (resolve or another
            // under-review). Mirror the "transition not valid" null contract so the service
            // layer re-resolves and reports the authoritative outcome (AlreadyResolved/NotFound).
            _logger.LogWarning(
                "dispute-case {CaseId} concurrent under-review transition at seq {Seq}; caller will re-resolve", latest.Id, nextSeq);
            return null;
        }

        return latest;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<DisputeCase?> ResolveLatestAsync(string caseId, CancellationToken ct)
    {
        var (row, _) = await ResolveLatestWithSeqAsync(caseId, ct);
        return row;
    }

    private async Task<(DisputeCase? Row, int Seq)> ResolveLatestWithSeqAsync(string caseId, CancellationToken ct)
    {
        // Base snapshot (seq 0).
        var baseOutcome = await _kv.GetAsync(RowKeyPrefix + caseId, ct);
        var row = Deserialize(baseOutcome?.ResponseBodyJson);
        if (row is null) return (null, -1);

        // Walk the append-only status chain forward; the highest present seq wins.
        var seq = 0;
        for (var next = 1; next <= MaxStatusRevisions; next++)
        {
            var rev = await _kv.GetAsync(StatusKeyPrefix + caseId + ":" + next, ct);
            var revRow = Deserialize(rev?.ResponseBodyJson);
            if (revRow is null) break;
            row = revRow;
            seq = next;
        }
        return (row, seq);
    }

    private async Task<IReadOnlyList<string>> ListIdsByPrefixAsync(string prefix, CancellationToken ct)
    {
        var outcomes = await _kv.FindByPrefixAsync(prefix, ct);
        // Index rows store the raw case id as their body.
        return outcomes
            .Select(o => o.ResponseBodyJson?.Trim().Trim('"'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Serialize(DisputeCase c) => JsonSerializer.Serialize(c, Json);

    private static DisputeCase? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<DisputeCase>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
