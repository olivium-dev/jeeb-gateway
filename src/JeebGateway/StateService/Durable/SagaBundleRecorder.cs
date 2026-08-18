using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Observability;

namespace JeebGateway.StateService.Durable;

/// <summary>
/// SPINE-FOUNDATION / ADR-006: records the delivery saga in the
/// jeeb-state-service bundle ledger (<c>saga_bundles</c>, additive table on the
/// existing <c>/v1/state</c> group). The gateway keeps NO order state of its
/// own on the durable path — this recorder is the only place the create flow
/// touches the ledger, and it does so through the state-service HTTP boundary
/// (never a direct DB connection): gateway-stateless + no-svc-bypass.
///
/// CONTRACT NOTE (regen-when-available): the <c>saga_bundles</c> routes are an
/// additive state-service extension that ships in a SEPARATE state-service PR.
/// Once that spec is published, <c>nswag-state.json</c> regenerates
/// <c>JeebStateServiceClient</c> with typed <c>CreateBundleAsync</c> /
/// <c>PatchBundleAsync</c> methods and this hand-written recorder is replaced
/// by a thin wrapper over the generated client (the NSwag freshness CI gate
/// guards that drift). Until then the recorder calls the documented routes
/// over a configured <see cref="HttpClient"/> so the gateway side can be built,
/// flag-gated OFF, and unit-tested ahead of the upstream deploy — it is never
/// on a hot path while <c>FeatureFlags:DurableRequests:Enabled=false</c>.
/// </summary>
public interface ISagaBundleRecorder
{
    /// <summary>
    /// Idempotently records the create step of a delivery saga. Keyed on
    /// <c>(source, sourceId)</c> server-side so a re-submit collapses onto the
    /// same bundle (the state-service returns 409/200 on the duplicate). Never
    /// throws on a state-service blip — returns the outcome so the durable
    /// store can degrade without failing the user's create.
    /// </summary>
    Task<SagaBundleRecordOutcome> RecordCreatedAsync(
        string sourceId,
        string tag,
        object state,
        CancellationToken ct);
}

/// <summary>Outcome of <see cref="ISagaBundleRecorder.RecordCreatedAsync"/>.</summary>
public enum SagaBundleRecordOutcome
{
    /// <summary>Bundle created (201).</summary>
    Recorded,

    /// <summary>Bundle already existed for <c>(source, sourceId)</c> (idempotent 409/200).</summary>
    AlreadyRecorded,

    /// <summary>State-service unreachable / 5xx — the row was NOT recorded; caller degrades.</summary>
    Unavailable,
}

/// <summary>
/// <see cref="HttpClient"/>-backed <see cref="ISagaBundleRecorder"/> targeting
/// the state-service <c>POST /v1/state/bundles</c> route. The HttpClient's
/// BaseAddress + resilience pipeline (retry / circuit-breaker / timeout) are
/// registered alongside the NSwag client so a state-service outage trips the
/// breaker and this recorder reports <see cref="SagaBundleRecordOutcome.Unavailable"/>
/// rather than cascading a 500 onto the create.
/// </summary>
public sealed class StateServiceSagaBundleRecorder : ISagaBundleRecorder
{
    /// <summary>
    /// Fixed <c>source</c> for every gateway-recorded saga bundle. Combined with
    /// the request id as <c>sourceId</c> it forms the server-side idempotency
    /// key so a retried create collapses onto one ledger row.
    /// </summary>
    public const string Source = "jeeb-gateway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<StateServiceSagaBundleRecorder> _logger;

    public StateServiceSagaBundleRecorder(
        HttpClient http,
        ILogger<StateServiceSagaBundleRecorder> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<SagaBundleRecordOutcome> RecordCreatedAsync(
        string sourceId,
        string tag,
        object state,
        CancellationToken ct)
    {
        var body = new SagaBundleCreateRequest
        {
            Tag = tag,
            Source = Source,
            SourceId = sourceId,
            State = state,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("v1/state/bundles", body, JsonOptions, ct);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Idempotent create: a bundle for (source, sourceId) already
                // exists. Not an error on the create path — the saga is already
                // recorded.
                return SagaBundleRecordOutcome.AlreadyRecorded;
            }

            if (response.IsSuccessStatusCode)
            {
                return SagaBundleRecordOutcome.Recorded;
            }

            // Any other non-2xx (incl. 5xx after retries/breaker) — degrade.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-saga-bundles"));
            _logger.LogWarning(
                "Saga bundle record for {SourceId} returned {Status}; degrading (create still succeeds).",
                sourceId, (int)response.StatusCode);
            return SagaBundleRecordOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            // Transport failure / breaker open / timeout. The gateway must NOT
            // fail the user's create because the durable ledger blipped — the
            // delivery row (the matching-resolve source of truth) is the hard
            // dependency; the bundle is the audit/saga trail.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-saga-bundles"));
            _logger.LogWarning(ex,
                "Saga bundle record for {SourceId} unavailable; degrading (create still succeeds).",
                sourceId);
            return SagaBundleRecordOutcome.Unavailable;
        }
    }
}

/// <summary>
/// Request body for state-service <c>POST /v1/state/bundles</c>. Mirrors the
/// cremat bundler create contract: opaque <c>state</c> JSONB keyed by
/// <c>(source, sourceId)</c>. The state-service owns no domain logic — it
/// stores the row and auto-appends prior state to <c>logs</c> on PATCH.
/// </summary>
public sealed class SagaBundleCreateRequest
{
    public required string Tag { get; init; }
    public required string Source { get; init; }
    public required string SourceId { get; init; }
    public required object State { get; init; }
}
