namespace JeebGateway.Requests;

/// <summary>
/// SPINE-FOUNDATION / ADR-006 feature flag for the stateless order/request
/// lifecycle. When <see cref="Enabled"/> is true the gateway's
/// <see cref="IRequestsStore"/> create path becomes durable: it seeds the
/// canonical delivery-service row (so <c>POST /matching/run</c> in request_id
/// mode resolves instead of 404-ing) and records the saga in the
/// jeeb-state-service bundle ledger — the gateway holds NO order state of its
/// own on that path.
///
/// Default is <b>false</b> so the green in-memory path (today's S05 H3 →
/// 201, S01–S04) is unchanged until the two upstreams (state-service
/// saga_bundles + delivery <c>POST /api/v1/deliveries</c>) are deployed and
/// smoke-passed. Flip via <c>FeatureFlags__DurableRequests__Enabled=true</c>
/// (a deploy <c>workflow_dispatch</c> input), staging-first.
///
/// This is a STORE-SELECTION cutover flag, deliberately NOT folded into
/// <see cref="JeebGateway.Services.UpstreamFeatureFlags"/> (which toggles
/// individual upstream proxies). It selects which <see cref="IRequestsStore"/>
/// implementation is registered — the durable decorator or the legacy
/// in-memory store — so the in-memory store stays as the instant rollback
/// lever until S05–S15 are green.
/// </summary>
public sealed class DurableRequestsOptions
{
    public const string SectionName = "FeatureFlags:DurableRequests";

    /// <summary>
    /// Master switch. Default <c>false</c> = today's green in-memory path.
    /// When true (and the durable dependencies are wired) the
    /// <see cref="DurableRequestsStore"/> decorator is registered ahead of the
    /// in-memory store.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Saga bundle <c>tag</c> recorded against every created request in the
    /// state-service ledger. Stable string so the ledger is queryable by tag.
    /// Overridable only for tests; production keeps the default.
    /// </summary>
    public string SagaTag { get; init; } = "delivery_saga_v1";

    /// <summary>
    /// Tenant scope forwarded to delivery-service when seeding the delivery
    /// row. Single-tenant today; mirrors MatchingController's
    /// <c>Services:Delivery:TenantId</c> default so the seeded row and the
    /// matching run resolve under the same tenant.
    /// </summary>
    public string TenantId { get; init; } = "default";

    /// <summary>
    /// JEB-50 (S05 H9b): the <c>phase</c> value logged to the jeeb-state bundler
    /// broadcast-log (<c>POST /v1/state/broadcasts</c>) when an order's
    /// broadcasting conversation is auto-created. MUST match the marker
    /// chat-service's <c>ResolvePhase</c> surfaces on the summary (the H9b
    /// <c>$.phase == "broadcasting"</c> assertion) so the durable log and the
    /// chat-read phase agree. Stable literal; overridable only for tests.
    /// </summary>
    public string BroadcastingPhase { get; init; } = "broadcasting";
}
