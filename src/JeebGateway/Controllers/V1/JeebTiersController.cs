using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// JEB-1431: V1 BFF read of the delivery tier catalog.
///
/// <c>GET /v1/tiers</c> — returns the full list of delivery tiers so the
/// mobile request-creation screen can render the tier picker. The response
/// includes each tier's SLA hours, catchment radius, commission rate, and
/// price hint. Public by design (same policy as the legacy
/// <see cref="JeebGateway.Controllers.TiersController"/>): the picker is
/// shown pre-login and no PII is involved.
///
/// When <c>FeatureFlags:UseUpstream:Delivery</c> is on the list is fetched from
/// delivery-service's <c>GET /api/v1/tiers</c>; otherwise the gateway-local
/// <see cref="ITiersStore"/> is used.
///
/// Coexists with the legacy (Obsolete) <see cref="JeebGateway.Controllers.TiersController"/>
/// at <c>/tiers</c>. New consumers should use this v1 surface.
/// </summary>
[ApiController]
// ADR-004 D1 / ADR-005 §A: the tier catalog is public — shown on the
// request-creation screen before the user is authenticated.
[AllowAnonymous]
[PublicEndpoint("Public delivery-tier catalog read — V1 (JEB-1431) — ADR-005 §A public.")]
public sealed class JeebTiersController : ControllerBase
{
    private readonly ITiersStore _store;
    private readonly IDeliveryServiceClient _upstream;
    private readonly UpstreamFeatureFlags _flags;

    public JeebTiersController(
        ITiersStore store,
        IDeliveryServiceClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _store = store;
        _upstream = upstream;
        _flags = flags.CurrentValue;
    }

    /// <summary>
    /// GET /v1/tiers — returns Flash/Express/Standard (and any other active tiers)
    /// with their SLA, radius, commission, and price-hint config. The mobile
    /// tier-picker calls this on the request-creation screen. Response is not
    /// paginated — the tier catalog is small and stable.
    /// </summary>
    [HttpGet("v1/tiers")]
    [ProducesResponseType(typeof(DeliveryTiersListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (_flags.Delivery)
        {
            var upstreamTiers = await _upstream.ListTiersAsync(ct);
            return Ok(new DeliveryTiersListResponse { Items = upstreamTiers });
        }

        var tiers = await _store.ListAsync(ct);
        return Ok(new DeliveryTiersListResponse
        {
            Items = tiers.Select(ToDto).ToList()
        });
    }

    internal static DeliveryTierDto ToDto(DeliveryTier t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        SlaHours = t.SlaHours,
        RadiusKm = t.RadiusKm,
        RequestTtlSeconds = t.RequestTtlSeconds,
        CommissionRate = t.CommissionRate,
        PriceHint = t.PriceHint,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };
}
