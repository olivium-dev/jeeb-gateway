using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Public read of the delivery tier catalog (T-backend-009). The mobile app
/// hits this on the request-creation screen to render the tier picker.
/// The five default tiers (Urgent, Same-Day, Scheduled, Economy, On-the-Way)
/// are seeded on startup; admins may edit the catalog via /admin/tiers and
/// changes are picked up on the next request.
/// </summary>
[ApiController]
[Route("tiers")]
public class TiersController : ControllerBase
{
    private readonly ITiersStore _store;

    public TiersController(ITiersStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(DeliveryTiersListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
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
