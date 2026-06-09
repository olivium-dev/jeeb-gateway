using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Matching;

/// <summary>
/// Default <see cref="IDeliveryRowMirror"/>. Composes the gateway in-memory
/// <see cref="IRequestsStore"/> read with the idempotent delivery-service
/// create-row call to seed the matching-resolve row just-in-time.
///
/// See <see cref="MatchingMirrorOptions"/> for the full rationale (the S06
/// "request lives only in the gateway store" root cause). This type is the
/// single, unit-testable seam for that orchestration so the controller stays a
/// thin BFF passthrough.
/// </summary>
public sealed class DeliveryRowMirror : IDeliveryRowMirror
{
    private readonly IRequestsStore _requests;
    private readonly IDeliveryServiceClient _delivery;
    private readonly MatchingMirrorOptions _options;
    private readonly string _tenantId;
    private readonly ILogger<DeliveryRowMirror> _logger;

    public DeliveryRowMirror(
        IRequestsStore requests,
        IDeliveryServiceClient delivery,
        IOptions<MatchingMirrorOptions> options,
        IConfiguration config,
        ILogger<DeliveryRowMirror> logger)
    {
        _requests = requests;
        _delivery = delivery;
        _options = options.Value;
        // Mirror MatchingController's tenant resolution so the seeded row and the
        // matching run resolve under the SAME tenant (delivery-service scopes the
        // WHERE id=$1 AND tenant_id=$2 lookup). Defaults to "default".
        _tenantId = config["Services:Delivery:TenantId"] ?? "default";
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MirrorOutcome> EnsureSeededAsync(string requestId, CancellationToken ct)
    {
        // (0) Instant rollback lever: when the flag is off, forward-only behaviour.
        if (!_options.Enabled)
        {
            return MirrorOutcome.Disabled;
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return MirrorOutcome.Skipped;
        }

        DeliveryRequest? request;
        try
        {
            request = await _requests.GetAsync(requestId, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate cancellation semantics by NOT seeding;
            // the run forward (also cancelled) will surface the cancellation.
            return MirrorOutcome.Skipped;
        }
        catch (Exception ex)
        {
            // A local-store read failure must never block matching. Log + skip.
            _logger.LogWarning(ex,
                "JIT delivery-row mirror: local store read failed for {RequestId}; skipping seed.",
                requestId);
            return MirrorOutcome.Failed;
        }

        // Unknown id (delivery-service will 404 canonically) or a row without the
        // pickup/tier the matching resolve needs → nothing useful to seed.
        if (request is null || request.PickupLocation is null || string.IsNullOrWhiteSpace(request.TierId))
        {
            return MirrorOutcome.Skipped;
        }

        try
        {
            // Seed ONLY the matching-resolve columns. The typed client treats a
            // 409 as idempotent success (ON CONFLICT (id) DO NOTHING), so this
            // composes cleanly with the create-time mirror and with a retried run.
            await _delivery.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
            {
                Id = request.Id,
                TenantId = _tenantId,
                ClientId = request.ClientId,
                TierId = request.TierId!,
                PickupLat = request.PickupLocation.Lat,
                PickupLng = request.PickupLocation.Lng,
            }, ct);

            return MirrorOutcome.Seeded;
        }
        catch (OperationCanceledException)
        {
            return MirrorOutcome.Skipped;
        }
        catch (Exception ex)
        {
            // BEST-EFFORT: swallow. delivery-service remains the canonical
            // authority for the matching outcome — if the row truly cannot be
            // resolved it returns its own 404, which the controller surfaces as
            // RFC 7807. We never convert a seed hiccup into a user-facing failure.
            _logger.LogWarning(ex,
                "JIT delivery-row mirror: seed failed for {RequestId}; forwarding run anyway.",
                requestId);
            return MirrorOutcome.Failed;
        }
    }
}
