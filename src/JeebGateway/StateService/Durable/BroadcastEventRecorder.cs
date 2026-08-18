using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Observability;

namespace JeebGateway.StateService.Durable;

/// <summary>
/// JEB-50 / S05 H9b: records the order-broadcast event in the jeeb-state-service
/// bundler broadcast-log (<c>broadcast_events</c>, additive append-only table on
/// the existing <c>/v1/state</c> group, route <c>POST /v1/state/broadcasts</c>).
///
/// <para>
/// The OWNER DIRECTIVE requires that when an order is broadcasting to candidate
/// jeebers, the broadcast event MUST be LOGGED to the bundler service for
/// cross-service management. This recorder is the gateway side of that log: when
/// the conversation provisioner has created a <c>broadcasting</c> channel for an
/// order, the gateway INDEPENDENTLY appends a broadcast-log row to the
/// state-service over its HTTP boundary (never a direct DB connection, never a
/// chat→state svc→svc call — cross-service composition lives only in the
/// gateway BFF). It is the durable, cross-service-visible record that the order
/// entered the <c>broadcasting</c> phase.
/// </para>
///
/// <para>
/// This mirrors <see cref="ISagaBundleRecorder"/> exactly: thin orchestration,
/// no gateway-held broadcast state (durability lives in state-service), and it
/// degrades — never fails the user's order create — on a state-service blip.
/// </para>
/// </summary>
public interface IBroadcastEventRecorder
{
    /// <summary>
    /// Appends a broadcast-log row for an order that entered the broadcasting
    /// phase. Keyed by <paramref name="contextId"/> (the conversation id) so the
    /// log can be queried per-order via
    /// <c>GET /v1/state/broadcasts?contextId={conversationId}</c>. Never throws on
    /// a state-service blip — returns the outcome so the durable create path can
    /// degrade without failing the order create.
    /// </summary>
    Task<BroadcastEventRecordOutcome> RecordBroadcastingAsync(
        string contextId,
        string phase,
        CancellationToken ct);
}

/// <summary>Outcome of <see cref="IBroadcastEventRecorder.RecordBroadcastingAsync"/>.</summary>
public enum BroadcastEventRecordOutcome
{
    /// <summary>Broadcast event appended (201).</summary>
    Recorded,

    /// <summary>State-service unreachable / 5xx — the row was NOT appended; caller degrades.</summary>
    Unavailable,
}

/// <summary>
/// <see cref="HttpClient"/>-backed <see cref="IBroadcastEventRecorder"/> targeting
/// the state-service <c>POST /v1/state/broadcasts</c> route. Registered with the
/// SAME state-service base URL + resilience pipeline (retry / breaker / timeout)
/// as <see cref="StateServiceSagaBundleRecorder"/> so a state-service outage trips
/// the breaker and this recorder reports
/// <see cref="BroadcastEventRecordOutcome.Unavailable"/> rather than cascading a
/// 500 onto the order create.
/// </summary>
public sealed class StateServiceBroadcastEventRecorder : IBroadcastEventRecorder
{
    /// <summary>
    /// Fixed <c>source</c> for every gateway-recorded broadcast event — matches
    /// the directive's <c>source:"jeeb-gateway"</c> contract and lets the bundler
    /// attribute the broadcast log to the gateway BFF.
    /// </summary>
    public const string Source = "jeeb-gateway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<StateServiceBroadcastEventRecorder> _logger;

    public StateServiceBroadcastEventRecorder(
        HttpClient http,
        ILogger<StateServiceBroadcastEventRecorder> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<BroadcastEventRecordOutcome> RecordBroadcastingAsync(
        string contextId,
        string phase,
        CancellationToken ct)
    {
        var body = new BroadcastEventCreateRequest
        {
            ContextId = contextId,
            Phase = phase,
            Source = Source,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("v1/state/broadcasts", body, JsonOptions, ct);

            if (response.IsSuccessStatusCode)
            {
                return BroadcastEventRecordOutcome.Recorded;
            }

            // Any non-2xx (incl. 5xx after retries/breaker) — degrade.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-broadcast-events"));
            _logger.LogWarning(
                "Broadcast event record for {ContextId} returned {Status}; degrading (order create still succeeds).",
                contextId, (int)response.StatusCode);
            return BroadcastEventRecordOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            // Transport failure / breaker open / timeout. The gateway must NOT
            // fail the user's order create because the broadcast LOG blipped —
            // the broadcasting conversation (which actually carries the phase) is
            // already created in chat; the broadcast_events row is the durable,
            // cross-service audit trail of that event.
            BusinessOutcomeTelemetry.DurableWriteFailures.Add(1,
                new KeyValuePair<string, object?>("store", "state-service-broadcast-events"));
            _logger.LogWarning(ex,
                "Broadcast event record for {ContextId} unavailable; degrading (order create still succeeds).",
                contextId);
            return BroadcastEventRecordOutcome.Unavailable;
        }
    }
}

/// <summary>
/// Request body for state-service <c>POST /v1/state/broadcasts</c>. Mirrors the
/// directive contract: <c>{contextId, phase, source}</c>. The state-service owns
/// the broadcast log — it appends an immutable row keyed by <c>contextId</c>; the
/// gateway holds no broadcast state of its own.
/// </summary>
public sealed class BroadcastEventCreateRequest
{
    public required string ContextId { get; init; }
    public required string Phase { get; init; }
    public required string Source { get; init; }
}
