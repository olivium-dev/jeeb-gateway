namespace JeebGateway.Matching;

/// <summary>
/// S06 (B1/B2/B3/ALT-2/ALT-3/ALT-4/ALT-4b/N5/N6): feature flag for the
/// just-in-time (JIT) delivery-row mirror that <see cref="JeebGateway.Controllers.MatchingController"/>
/// performs immediately before forwarding a <c>requestId</c>-mode
/// <c>POST /matching/run</c> to delivery-service.
///
/// WHY THIS EXISTS (the S06 root cause): the gateway holds the request row in its
/// in-memory <see cref="JeebGateway.Requests.IRequestsStore"/>, but
/// delivery-service — which owns the matching domain + the <c>deliveries</c> table
/// the matching run resolves against — has no record of it, so
/// <c>POST /api/v1/matching/run</c> in request_id mode returns
/// <c>404 unknown_request_id</c>. The create-time mirror in
/// <see cref="JeebGateway.Requests.DurableRequestsStore"/> closes the same gap, but
/// it is coupled to the heavy <c>FeatureFlags:DurableRequests</c> switch (which
/// also pulls in the state-service saga ledger + conversation auto-create). This
/// JIT mirror is the lighter, decoupled lever: it seeds ONLY the matching-resolve
/// columns <c>(id, tenant_id, client_id, tier_id, pickup_lat/lng)</c> via the SAME
/// idempotent typed-client call (<c>POST /api/v1/deliveries</c>,
/// <c>ON CONFLICT (id) DO NOTHING</c>) right before the run, so matching resolves
/// without arming the full durable spine.
///
/// LOOSE COUPLING (org-law): this is thin BFF orchestration in the gateway only —
/// the gateway composes two existing delivery-service typed-client calls
/// (seed-row → run-matching). delivery-service still owns the delivery/matching
/// domain + data; the gateway never reaches into another service's DB and never
/// couples two microservices to each other.
///
/// BEST-EFFORT: the seed is wrapped so a seed failure NEVER changes the
/// matching-run outcome beyond what delivery-service itself would return — if the
/// row genuinely cannot be seeded/resolved, delivery-service still owns the
/// canonical 404/422. Idempotent + composes cleanly with the create-time mirror.
///
/// Default <see cref="Enabled"/> is <b>true</b> so the S06 reds resolve out of the
/// box; flip to false (a deploy <c>workflow_dispatch</c> input via
/// <c>FeatureFlags__MatchingMirror__Enabled=false</c>) for an instant rollback to
/// the pre-S06 forward-only behaviour.
/// </summary>
public sealed class MatchingMirrorOptions
{
    public const string SectionName = "FeatureFlags:MatchingMirror";

    /// <summary>
    /// Master switch for the JIT pre-run delivery-row seed. Default <c>true</c>.
    /// When false the controller forwards to delivery-service exactly as before
    /// (no pre-run seed) — the instant rollback lever.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
