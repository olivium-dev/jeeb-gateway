using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Public read of the delivery tier catalog (T-backend-009). The mobile app
/// hits this on the request-creation screen to render the tier picker.
/// The five default tiers (Urgent, Same-Day, Scheduled, Economy, On-the-Way)
/// are seeded on startup; admins may edit the catalog via /admin/tiers and
/// changes are picked up on the next request.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("tiers")]
// ADR-004 D1: public by design — the public tier catalog the mobile app renders pre-login
// on the request-creation screen (admin tier CRUD lives in the separate AdminTiersController,
// which remains session/role-gated). Invariant: Get_Tiers_Does_Not_Require_Authentication.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public class TiersController : ControllerBase
{
    private readonly ITiersStore _store;
    private readonly IDeliveryServiceClient _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public TiersController(
        ITiersStore store,
        IDeliveryServiceClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _store = store;
        _upstream = upstream;
        _flags = flags;
    }

    [HttpGet]
    [ProducesResponseType(typeof(DeliveryTiersListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (_flags.CurrentValue.Delivery)
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
        CommissionRate = t.CommissionRate,
        PriceHint = t.PriceHint,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt
    };
}
