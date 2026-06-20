using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Disputes;

/// <summary>
/// Durable <see cref="IDisputeStore"/> backed by <b>jeeb-state-service</b> (ADR-0001:
/// the gateway is STATELESS &amp; THIN — it must hold no dispute row in process memory).
/// Replaces <see cref="InMemoryDisputeStore"/>, whose rows evaporated on every gateway
/// bounce / replica move (the ADR-0001 violation flagged for remediation in run #1).
///
/// <para><b>Why the opaque KV (not the typed state-service dispute domain).</b> The
/// state-service ships a first-class dispute primitive
/// (<c>POST /v1/state/disputes</c> + <c>/transition</c>), but its row is the GENERIC
/// envelope <c>(caseId, contextId, status, version, openedBy)</c> — it deliberately
/// does NOT carry the Jeeb-specific fields the mobile dispute surface needs
/// (category, free-text description, evidence photo URLs, resolution notes). Putting
/// those Jeeb fields downstream would leak product semantics into the generic service
/// (Golden-Rule-2 violation). So the FULL Jeeb dispute row is persisted as one OPAQUE
/// JSON body in the state-service idempotency KV — the same general key→opaque-body
/// store with GET-by-key + prefix-scan that <see cref="JeebGateway.JeebSupport.StateServiceSupportTicketStore"/>
/// (PR #206) and the durable offer-request index already reuse. The gateway holds NO
/// row itself; everything survives a bounce.</para>
///
/// <para><b>Mutable state on an insert-once KV.</b> The KV is exactly-once
/// (<c>INSERT … ON CONFLICT (key) DO NOTHING</c>) — a key cannot be overwritten. A
/// dispute's <see cref="DisputeState"/> is mutable (filed → under_review →
/// resolved/dismissed), so state changes are modelled as an APPEND-ONLY chain of
/// version keys <c>dispute-status:{id}:{seq}</c>, each carrying the full row snapshot
/// at that revision. The immutable base row lives at <c>dispute:{id}</c> (seq 0). A
/// read resolves the row by taking the highest existing seq — the same monotone-write
/// pattern a write-once log uses to express mutation. The handful of admin transitions
/// per dispute keeps the chain short.</para>
///
/// <para><b>Indexes.</b> Two secondary-index families are written alongside the row so
/// the two non-by-id reads need only a prefix scan (no full-table scan):
/// <list type="bullet">
///   <item><c>dispute-owner:{userId}:{id}</c> → <see cref="ListForUserAsync"/>.</item>
///   <item><c>dispute-delivery:{deliveryId}:{id}</c> → <see cref="GetOpenForDeliveryAsync"/>.</item>
/// </list>
/// Index rows are written ONCE at file time (immutable id↔owner / id↔delivery edges);
/// the live state is always re-read from the status chain so a stale index value never
/// masks a transition.</para>
///
/// <para><b>TTL.</b> 180 days — longer than a dispute's useful life while bounding KV growth.</para>
/// </summary>
public sealed class StateServiceDisputeStore : IDisputeStore
{
    internal const string RowKeyPrefix = "dispute:";
    internal const string StatusKeyPrefix = "dispute-status:";
    internal const string OwnerKeyPrefix = "dispute-owner:";
    internal const string DeliveryKeyPrefix = "dispute-delivery:";

    /// <summary>180-day TTL (seconds).</summary>
    internal const int TtlSeconds = 180 * 24 * 60 * 60;

    /// <summary>Defensive upper bound on the per-dispute status chain length.</summary>
    private const int MaxStatusRevisions = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _kv;
    private readonly ILogger<StateServiceDisputeStore> _logger;

    public StateServiceDisputeStore(IIdempotencyStore kv, ILogger<StateServiceDisputeStore> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    public async Task<Dispute> AddAsync(Dispute dispute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dispute);

        var body = Serialize(dispute);

        // Base (seq 0) immutable snapshot — the by-id anchor.
        await _kv.PutOrGetAsync(RowKeyPrefix + dispute.Id, statusCode: 201, body, TtlSeconds, ct);

        // Owner + delivery indexes (immutable edges) so list-by-owner /
        // open-for-delivery are prefix scans, not full scans.
        if (!string.IsNullOrWhiteSpace(dispute.FiledByUserId))
            await _kv.PutOrGetAsync(
                OwnerKeyPrefix + dispute.FiledByUserId + ":" + dispute.Id, 201, dispute.Id, TtlSeconds, ct);
        if (!string.IsNullOrWhiteSpace(dispute.DeliveryId))
            await _kv.PutOrGetAsync(
                DeliveryKeyPrefix + dispute.DeliveryId + ":" + dispute.Id, 201, dispute.Id, TtlSeconds, ct);

        return dispute;
    }

    public async Task<Dispute?> GetByIdAsync(string disputeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(disputeId)) return null;
        return await ResolveLatestAsync(disputeId.Trim(), ct);
    }

    public async Task<IReadOnlyList<Dispute>> ListForUserAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<Dispute>();

        var ids = await ListIdsByPrefixAsync(OwnerKeyPrefix + userId + ":", ct);
        var rows = new List<Dispute>(ids.Count);
        foreach (var id in ids)
        {
            var row = await ResolveLatestAsync(id, ct);
            if (row is not null) rows.Add(row);
        }
        return rows
            .OrderByDescending(d => d.FiledAt)
            .ToList();
    }

    public async Task<Dispute?> GetOpenForDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return null;

        var ids = await ListIdsByPrefixAsync(DeliveryKeyPrefix + deliveryId + ":", ct);
        Dispute? open = null;
        foreach (var id in ids)
        {
            var row = await ResolveLatestAsync(id, ct);
            if (row is null || DisputeState.IsTerminal(row.State)) continue;
            if (open is null || row.FiledAt > open.FiledAt) open = row;
        }
        return open;
    }

    public async Task<Dispute?> UpdateStateAsync(string disputeId, DisputeStatePatch patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(disputeId)) return null;

        var (latest, seq) = await ResolveLatestWithSeqAsync(disputeId.Trim(), ct);
        if (latest is null) return null;

        // Apply the patch onto a fresh snapshot (the base row's non-status fields are
        // immutable; only State/ReviewedAt/ResolverAdminId/Resolution change).
        latest.State = patch.State;
        latest.ReviewedAt = patch.ReviewedAt;
        latest.ResolverAdminId = patch.ResolverAdminId;
        latest.Resolution = patch.Resolution;

        // Append the new revision. Insert-once on the next seq key gives a natural
        // optimistic-concurrency guard: if a concurrent writer already took seq+1 the
        // PutOrGet returns Inserted=false and we surface a conflict by retrying the read.
        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.Id + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);

        if (!outcome.Inserted)
        {
            // Lost the race for this seq — re-resolve and return the authoritative row.
            _logger.LogWarning(
                "dispute {DisputeId} concurrent transition at seq {Seq}; returning authoritative latest", latest.Id, nextSeq);
            return await ResolveLatestAsync(latest.Id, ct);
        }

        return latest;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<Dispute?> ResolveLatestAsync(string disputeId, CancellationToken ct)
        => ResolveLatestWithSeqAsync(disputeId, ct).ContinueWith(t => t.Result.Row, ct,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private async Task<(Dispute? Row, int Seq)> ResolveLatestWithSeqAsync(string disputeId, CancellationToken ct)
    {
        // Base snapshot (seq 0).
        var baseOutcome = await _kv.GetAsync(RowKeyPrefix + disputeId, ct);
        var row = Deserialize(baseOutcome?.ResponseBodyJson);
        if (row is null) return (null, -1);

        // Walk the append-only status chain forward; the highest present seq wins.
        var seq = 0;
        for (var next = 1; next <= MaxStatusRevisions; next++)
        {
            var rev = await _kv.GetAsync(StatusKeyPrefix + disputeId + ":" + next, ct);
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
        // Index rows store the raw dispute id as their body.
        return outcomes
            .Select(o => o.ResponseBodyJson?.Trim().Trim('"'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Serialize(Dispute d) => JsonSerializer.Serialize(d, Json);

    private static Dispute? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<Dispute>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
