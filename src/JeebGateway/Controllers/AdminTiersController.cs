using JeebGateway.Tiers;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin CRUD for the delivery tier catalog (T-backend-009). Tier changes
/// take effect on the next request — the in-memory store returns deep-cloned
/// snapshots on every read so an in-flight request can never observe a
/// half-updated tier.
/// </summary>
[ApiController]
[Route("admin/tiers")]
[RequireRole(Roles.Admin)]
public class AdminTiersController : ControllerBase
{
    private const int MaxNameLength = 100;
    private const int MaxPriceHintLength = 500;
    private const int MaxSlaHours = 30 * 24; // 30 days
    private const double MaxRadiusKm = 1000.0;

    private readonly ITiersStore _store;

    public AdminTiersController(ITiersStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(DeliveryTiersListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tiers = await _store.ListAsync(ct);
        return Ok(new DeliveryTiersListResponse
        {
            Items = tiers.Select(TiersController.ToDto).ToList()
        });
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DeliveryTierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var t = await _store.GetAsync(id, ct);
        if (t is null) return NotFound();
        return Ok(TiersController.ToDto(t));
    }

    [HttpPost]
    [ProducesResponseType(typeof(DeliveryTierDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] DeliveryTierCreateRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body is null) return BadRequestBody();
        if (ValidateName(body.Name, out var err) is false) return err!;
        if (ValidateSlaHours(body.SlaHours, out err) is false) return err!;
        if (ValidateRadius(body.RadiusKm, out err) is false) return err!;
        if (ValidateCommission(body.CommissionRate, out err) is false) return err!;
        if (ValidatePriceHint(body.PriceHint, out err) is false) return err!;
        if (body.Id is not null && ValidateId(body.Id, out err) is false) return err!;

        try
        {
            var created = await _store.CreateAsync(new DeliveryTierCreate
            {
                Id = body.Id,
                Name = body.Name!,
                SlaHours = body.SlaHours!.Value,
                RadiusKm = body.RadiusKm!.Value,
                CommissionRate = body.CommissionRate!.Value,
                PriceHint = body.PriceHint!
            }, adminId, ct);

            return CreatedAtAction(nameof(Get), new { id = created.Id }, TiersController.ToDto(created));
        }
        catch (DuplicateTierIdException ex)
        {
            return Conflict(new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status409Conflict });
        }
        catch (DuplicateTierNameException ex)
        {
            return Conflict(new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status409Conflict });
        }
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(DeliveryTierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Replace(string id, [FromBody] DeliveryTierReplaceRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body is null) return BadRequestBody();
        if (ValidateName(body.Name, out var err) is false) return err!;
        if (ValidateSlaHours(body.SlaHours, out err) is false) return err!;
        if (ValidateRadius(body.RadiusKm, out err) is false) return err!;
        if (ValidateCommission(body.CommissionRate, out err) is false) return err!;
        if (ValidatePriceHint(body.PriceHint, out err) is false) return err!;

        try
        {
            var updated = await _store.ReplaceAsync(id, new DeliveryTierReplace
            {
                Name = body.Name!,
                SlaHours = body.SlaHours!.Value,
                RadiusKm = body.RadiusKm!.Value,
                CommissionRate = body.CommissionRate!.Value,
                PriceHint = body.PriceHint!
            }, adminId, ct);

            if (updated is null) return NotFound();
            return Ok(TiersController.ToDto(updated));
        }
        catch (DuplicateTierNameException ex)
        {
            return Conflict(new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status409Conflict });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var removed = await _store.DeleteAsync(id, ct);
        if (!removed) return NotFound();
        return NoContent();
    }

    private bool ValidateId(string id, out IActionResult? error)
    {
        if (!InMemoryTiersStore.IsValidSlug(id))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "id must match ^[a-z][a-z0-9-]{1,47}$ (lowercase slug, 2-48 chars).",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateName(string? name, out IActionResult? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "name is required.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"name must be {MaxNameLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateSlaHours(int? sla, out IActionResult? error)
    {
        if (sla is null || sla < 1 || sla > MaxSlaHours)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"sla_hours must be between 1 and {MaxSlaHours}.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateRadius(double? radius, out IActionResult? error)
    {
        if (radius is null || double.IsNaN(radius.Value) || radius <= 0 || radius > MaxRadiusKm)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"radius_km must be > 0 and <= {MaxRadiusKm}.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateCommission(double? rate, out IActionResult? error)
    {
        if (rate is null || double.IsNaN(rate.Value) || rate < 0 || rate > 1)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "commission_rate must be between 0 and 1 (inclusive).",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidatePriceHint(string? hint, out IActionResult? error)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "price_hint is required.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        if (hint.Length > MaxPriceHintLength)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"price_hint must be {MaxPriceHintLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private IActionResult BadRequestBody() => BadRequest(new ProblemDetails
    {
        Title = "Request body is required.",
        Status = StatusCodes.Status400BadRequest
    });
}
