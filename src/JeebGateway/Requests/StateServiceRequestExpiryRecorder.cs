using JeebGateway.Observability;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Requests;

/// <summary>
/// JEB-1508: <see cref="IRequestExpiryRecorder"/> backed by the jeeb-state-service
/// idempotency KV (R1 surface: <c>PUT/GET /idempotency</c>). The idempotency store
/// is the right persistence surface here because:
/// <list type="bullet">
///   <item>R1 is a general-purpose key→opaque-body store with GET-by-key.</item>
///   <item>It is already wired and carries its own Polly retry/circuit-breaker.</item>
///   <item>Expiry records are small (requestId + timestamp) and TTL'd naturally.</item>
/// </list>
///
/// Key scheme: <c>request-expired:{requestId}</c> — namespace-prefixed so the
/// routing never collides with real idempotency-key records
/// (same convention as <c>offer-routing:{offerId}</c> in
/// <see cref="StateServiceOfferRequestIndex"/>).
///
/// DEGRADE-DON'T-FAIL: a state-service blip logs a warning and leaves the expiry
/// in-memory only — the sweeper's in-memory transition has already committed and
/// the delivery contract is unchanged. The durable record is the audit/replay trail,
/// not the source of truth for the live state machine.
///
/// TTL: 72 hours — long enough to cover any reasonable restart window for
/// hydration; short enough to bound KV growth.
/// </summary>
public sealed class StateServiceRequestExpiryRecorder : IRequestExpiryRecorder
{
    internal const string KeyPrefix = "request-expired:";

    /// <summary>
    /// 72-hour TTL: expires requests are terminal; the record only needs to
    /// survive a restart/re-deploy window for hydration. After that the row
    /// carries no operational value and can be evicted.
    /// </summary>
    internal const int TtlSeconds = 72 * 60 * 60;

    private readonly IIdempotencyStore _durable;
    private readonly ILogger<StateServiceRequestExpiryRecorder> _logger;

    public StateServiceRequestExpiryRecorder(
        IIdempotencyStore durable,
        ILogger<StateServiceRequestExpiryRecorder> logger)
    {
        _durable = durable;
        _logger = logger;
    }

    public async Task RecordExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        try
        {
            var bodyJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "expired",
                request_id = requestId,
                expired_at = expiredAt
            });

            // PutOrGet is idempotent: a duplicate call for the same request is a no-op.
            await _durable.PutOrGetAsync(
                key: KeyPrefix + requestId,
                statusCode: 200,
                responseBodyJson: bodyJson,
                ttlSeconds: TtlSeconds,
                ct);
        }
        catch (Exception ex)
        {
            // Best-effort mirror. A state-service blip must not fail the sweep or
            // roll back the already-committed in-memory expiry — the local transition
            // is the source of truth; this record is the durable audit trail.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-request-expiry"));
            _logger.LogWarning(ex,
                "Failed to persist expiry record for request {RequestId} to state-service; " +
                "expiry stands in-memory but will not survive a gateway restart.",
                requestId);
        }
    }
}
