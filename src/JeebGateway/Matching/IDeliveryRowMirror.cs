namespace JeebGateway.Matching;

/// <summary>
/// S06 just-in-time delivery-row mirror. Given a gateway request id, best-effort
/// seeds the canonical delivery-service <c>deliveries</c> row (the matching-resolve
/// source of truth) immediately before <c>POST /matching/run</c> forwards to
/// delivery-service — so a request that lives only in the gateway's in-memory
/// store resolves instead of 404-ing.
///
/// THIN BFF / LOOSE COUPLING: the implementation composes the gateway's in-memory
/// <see cref="JeebGateway.Requests.IRequestsStore"/> read with the existing
/// <see cref="JeebGateway.Services.Clients.IDeliveryServiceClient.CreateDeliveryRowAsync"/>
/// (idempotent <c>POST /api/v1/deliveries</c>). It holds no domain logic of its own.
///
/// BEST-EFFORT CONTRACT: <see cref="EnsureSeededAsync"/> NEVER throws. Any failure
/// (flag off, request not found locally, missing pickup/tier, upstream error,
/// cancellation) returns a non-fatal <see cref="MirrorOutcome"/> and lets the
/// caller proceed to delivery-service, which remains the canonical authority for
/// the matching result (including a genuine 404 when the row truly cannot resolve).
/// </summary>
public interface IDeliveryRowMirror
{
    /// <summary>
    /// Best-effort seed the delivery row for <paramref name="requestId"/>. Returns
    /// the outcome for observability; the caller treats every outcome as
    /// non-blocking and proceeds to forward the matching run.
    /// </summary>
    Task<MirrorOutcome> EnsureSeededAsync(string requestId, CancellationToken ct);
}

/// <summary>
/// Outcome of a JIT mirror attempt. Diagnostic only — the caller never branches
/// its HTTP response on this; it forwards to delivery-service regardless.
/// </summary>
public enum MirrorOutcome
{
    /// <summary>The JIT mirror flag is off; no seed attempted.</summary>
    Disabled,

    /// <summary>The row was seeded (2xx) or already existed (409 idempotent).</summary>
    Seeded,

    /// <summary>
    /// No locally-known request matched <c>requestId</c>, or it lacked the
    /// pickup/tier needed to back the matching resolve. No seed attempted; the
    /// dry-run/preview shape and unknown ids fall through to delivery-service.
    /// </summary>
    Skipped,

    /// <summary>
    /// The seed attempt failed (upstream error / transient). Swallowed — the
    /// caller still forwards the run and delivery-service owns the final outcome.
    /// </summary>
    Failed,
}
