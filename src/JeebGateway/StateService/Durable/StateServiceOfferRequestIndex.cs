using System.Text.Json;
using JeebGateway.Availability;
using JeebGateway.Observability;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.StateService.Durable;

/// <summary>
/// S08 (A3/N9) — DURABLE, bounce- and replica-survivable <see cref="IOfferRequestIndex"/>.
///
/// <para><b>The problem this fixes.</b> The mobile offer-edit and offer-accept routes are
/// offer-scoped (<c>PUT /v1/offers/{offerId}</c>, <c>POST /offers/{offerId}/accept</c>)
/// while the canonical offer-service routes are request-scoped, and offer-service exposes
/// NO get-offer-by-id route to recover the requestId from the offer. The gateway therefore
/// learns the <c>offerId → (requestId, jeeberId)</c> pairing at submit time and resolves it
/// at edit/accept. The previous <see cref="InMemoryOfferRequestIndex"/> held that pairing in
/// a process-local <c>ConcurrentDictionary</c>, so it was LOST on a gateway bounce and NOT
/// shared across replicas: an offer submitted to replica A then edited/accepted on replica B
/// (or after a redeploy) resolved to <c>null</c> and returned a spurious 404 even though the
/// offer was perfectly live in offer-service.</para>
///
/// <para><b>The fix.</b> This decorator keeps the in-memory index as the fast,
/// authoritative-within-instance read/write model and additionally MIRRORS each pairing into
/// jeeb-state-service's durable idempotency KV (R1 — keyed by an arbitrary string, GET-by-key,
/// TTL'd). On a resolve MISS in the in-memory cache (cold replica / post-bounce) it reads the
/// pairing back from state-service and re-hydrates the local cache. The pairing is a pure
/// STRUCTURAL routing fact (offerId → requestId/jeeberId) — no fee, status, ownership, or
/// lifecycle — so this is still a thin-BFF routing concern, never auction domain state
/// (microservices-no-inter-service-coupling: composition in the BFF, domain state in the
/// owning service).</para>
///
/// <para><b>DEGRADE-DON'T-FAIL.</b> A state-service blip must never turn an offer submit /
/// edit / accept into a 5xx. Writes are best-effort mirrors (a failed mirror only means the
/// pairing won't survive a bounce, exactly the pre-fix behaviour). Reads fall through to the
/// in-memory cache and, on a durable-store fault, to <c>null</c> — the SAME phantom-offer 404
/// contract the in-memory index already produced for an unknown offer. The state-service's own
/// Polly resilience pipeline (retry + circuit breaker) absorbs transient faults.</para>
///
/// <para><b>Why the idempotency KV.</b> The state-service contract is a fixed set of
/// domain-keyed aggregates (idempotency, KYC, ratings, disputes, strikes, locks); only the R1
/// idempotency surface (<c>PUT/GET /idempotency</c>) is a general key→opaque-body store with
/// GET-by-key. Reusing it here needs ZERO upstream service change and ZERO new migration —
/// the strictly correct durable fix (an offer-service get-offer-by-id route, or a dedicated
/// state-service offer-routing aggregate) is an upstream change tracked as a separate follow-up.
/// We namespace the key (<c>offer-routing:{offerId}</c>) so it never collides with real
/// request idempotency keys.</para>
/// </summary>
public sealed class StateServiceOfferRequestIndex : IOfferRequestIndex
{
    /// <summary>
    /// Key namespace so the routing pairing never collides with real request
    /// Idempotency-Key records in the shared KV.
    /// </summary>
    internal const string KeyPrefix = "offer-routing:";

    /// <summary>
    /// F2 — reverse (jeeber → offer ids) index namespace. One durable KV row per
    /// (jeeberId, offerId) pair, keyed <c>offer-routing-jeeber:{jeeberId}:{offerId}</c>,
    /// body = the offerId. Enumerated with <see cref="IIdempotencyStore.FindByPrefixAsync"/>
    /// on a cold <see cref="ListOfferIdsForJeeber"/> miss so a jeeber's own bids survive a
    /// bounce / replica move. Distinct from <see cref="KeyPrefix"/> (<c>offer-routing:</c>):
    /// the char after <c>offer-routing</c> is <c>-</c> here vs <c>:</c> there, so a
    /// forward GET-by-key and a reverse prefix-scan never cross-match.
    /// </summary>
    internal const string ReverseKeyPrefix = "offer-routing-jeeber:";

    private static string ReverseKey(string jeeberId, string offerId)
        => ReverseKeyPrefix + jeeberId + ":" + offerId;

    /// <summary>
    /// 7-day TTL — comfortably longer than any auction lifetime (a request that is still
    /// pre-acceptance after a week has expired). Bounds the KV growth; an offer resolved
    /// after expiry simply 404s as a phantom, the correct contract for a stale offer.
    /// </summary>
    internal const int TtlSeconds = 7 * 24 * 60 * 60;

    private readonly InMemoryOfferRequestIndex _local;
    private readonly IIdempotencyStore _durable;
    private readonly ILogger<StateServiceOfferRequestIndex> _logger;

    public StateServiceOfferRequestIndex(
        InMemoryOfferRequestIndex local,
        IIdempotencyStore durable,
        ILogger<StateServiceOfferRequestIndex> logger)
    {
        _local = local;
        _durable = durable;
        _logger = logger;
    }

    public void Record(string offerId, string requestId)
        => Record(offerId, requestId, jeeberId: null);

    public void Record(string offerId, string requestId, string? jeeberId)
    {
        if (string.IsNullOrWhiteSpace(offerId) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        // Authoritative, synchronous local write (unchanged semantics).
        _local.Record(offerId, requestId, jeeberId);

        // Best-effort durable mirror. Fire-and-forget so the offer-submit hot path is not
        // gated on a state-service round-trip; a failure only costs bounce-survivability,
        // never the 201. The state-service client carries its own retry/breaker/timeout.
        var normalizedJeeberId = string.IsNullOrWhiteSpace(jeeberId) ? null : jeeberId;
        _ = MirrorAsync(offerId, requestId, normalizedJeeberId);

        // F2: additionally mirror the reverse (jeeber → offerId) pair so the jeeber's
        // own bids ("my-offers") survive a bounce / cold replica. Best-effort,
        // fire-and-forget — a failure only forfeits reverse-lookup bounce-survivability
        // for THIS pair, never the 201 (identical posture to the forward mirror).
        if (normalizedJeeberId is not null)
        {
            _ = MirrorReverseAsync(normalizedJeeberId, offerId);
        }
    }

    public string? ResolveRequestId(string offerId)
        => Resolve(offerId)?.RequestId;

    public string? ResolveJeeberId(string offerId)
        => Resolve(offerId)?.JeeberId;

    /// <summary>
    /// F2 — reverse (jeeber → offer ids) lookup. The fast in-memory cache is
    /// authoritative-within-instance; on a COLD read (post-bounce / cross-replica) it
    /// is empty, so we additionally enumerate the durable reverse index
    /// (<see cref="ReverseKeyPrefix"/>) and UNION its offer ids in. A durable-store
    /// blip degrades to the in-memory result — never a fault — exactly the pre-fix
    /// "no locally-known offers" contract, only now bounce-survivable.
    /// </summary>
    public IReadOnlyList<string> ListOfferIdsForJeeber(string jeeberId)
    {
        var local = _local.ListOfferIdsForJeeber(jeeberId);
        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return local;
        }

        var durable = ReadDurableReverse(jeeberId);
        if (durable.Count == 0)
        {
            return local;
        }

        // Union local ∪ durable, preserving order (local first) and de-duping.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>(local.Count + durable.Count);
        foreach (var id in local)
        {
            if (seen.Add(id)) merged.Add(id);
        }
        foreach (var id in durable)
        {
            if (seen.Add(id)) merged.Add(id);
        }
        return merged;
    }

    // ------------------------------------------------------------------
    // internals
    // ------------------------------------------------------------------

    private sealed record Pairing(string RequestId, string? JeeberId);

    /// <summary>
    /// Resolve from the local cache first; on a miss (cold replica / post-bounce) re-hydrate
    /// from the durable store synchronously. The resolve sites (offer edit/accept) are not on
    /// a latency-critical hot loop, so a single blocking durable read on a cache miss is an
    /// acceptable cost to convert a spurious 404 into a correct resolve.
    /// </summary>
    private Pairing? Resolve(string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId))
        {
            return null;
        }

        var localRequestId = _local.ResolveRequestId(offerId);
        if (!string.IsNullOrWhiteSpace(localRequestId))
        {
            return new Pairing(localRequestId, _local.ResolveJeeberId(offerId));
        }

        // Local miss — read the durable mirror and re-hydrate the cache.
        var durable = ReadDurable(offerId);
        if (durable is not null)
        {
            _local.Record(offerId, durable.RequestId, durable.JeeberId);
        }
        return durable;
    }

    private async Task MirrorAsync(string offerId, string requestId, string? jeeberId)
    {
        try
        {
            var bodyJson = JsonSerializer.Serialize(new Pairing(requestId, jeeberId));
            // PutOrGet is idempotent on the key: re-recording the same offer is a no-op mirror,
            // matching the in-memory index's idempotent Record. statusCode is unused for routing
            // (carried only so the KV row is well-formed); we read back ResponseBody, not status.
            await _durable.PutOrGetAsync(KeyPrefix + offerId, statusCode: 200, bodyJson, TtlSeconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Mirror failure only forfeits bounce-survivability for THIS pairing; the local
            // index still resolves it within the instance. Never throws into the 201 path.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-offer-routing"));
            _logger.LogWarning(ex,
                "Durable mirror of offer-routing pairing {OfferId} -> {RequestId} failed; "
                + "the pairing stays in-memory only and will not survive a gateway bounce.",
                offerId, requestId);
        }
    }

    private async Task MirrorReverseAsync(string jeeberId, string offerId)
    {
        try
        {
            // Body = the offerId verbatim. PutOrGet is insert-once idempotent on the
            // key, so re-recording the same (jeeber, offer) pair is a no-op mirror.
            await _durable.PutOrGetAsync(
                ReverseKey(jeeberId, offerId), statusCode: 200, offerId, TtlSeconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-offer-routing"));
            _logger.LogWarning(ex,
                "Durable mirror of reverse offer-routing pair jeeber {JeeberId} -> {OfferId} failed; "
                + "the jeeber's my-offers list will not survive a gateway bounce for this offer.",
                jeeberId, offerId);
        }
    }

    private IReadOnlyList<string> ReadDurableReverse(string jeeberId)
    {
        try
        {
            var rows = _durable
                .FindByPrefixAsync(ReverseKeyPrefix + jeeberId + ":", CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (rows is null || rows.Count == 0)
            {
                return Array.Empty<string>();
            }

            var offerIds = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.ResponseBodyJson))
                {
                    offerIds.Add(row.ResponseBodyJson);
                }
            }
            return offerIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Durable reverse read of offer-routing pairs for jeeber {JeeberId} failed; "
                + "returning the in-memory-only my-offers set.",
                jeeberId);
            return Array.Empty<string>();
        }
    }

    private Pairing? ReadDurable(string offerId)
    {
        try
        {
            // The resolve sites are synchronous (IOfferRequestIndex is a sync contract), so we
            // block on the single durable read. The state-service client's timeout + breaker
            // bound this; a fault returns null (phantom-offer 404), never a hang.
            var outcome = _durable
                .GetAsync(KeyPrefix + offerId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (outcome is null || string.IsNullOrWhiteSpace(outcome.ResponseBodyJson))
            {
                return null;
            }

            var pairing = JsonSerializer.Deserialize<Pairing>(outcome.ResponseBodyJson);
            return string.IsNullOrWhiteSpace(pairing?.RequestId) ? null : pairing;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Durable read of offer-routing pairing for {OfferId} failed; "
                + "resolving as unknown (phantom-offer 404 contract).",
                offerId);
            return null;
        }
    }
}
