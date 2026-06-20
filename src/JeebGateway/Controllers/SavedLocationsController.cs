using System.Security.Claims;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users.SavedLocations;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// WS-02 — Saved Locations BFF (ACCT-04 / REQ-02). Thin, self-scoped CRUD over
/// the caller's own saved locations. Identity is resolved from the caller (claim
/// or X-User-Id), so the route is <c>me</c>-scoped; no userId path segment is
/// trusted from the client. Backed by <see cref="ISavedLocationStore"/>
/// (in-memory today — net-new, no upstream yet).
/// </summary>
[ApiController]
[Route("api/users/me/saved-locations")]
public class SavedLocationsController : ControllerBase
{
    private readonly ISavedLocationStore _store;

    public SavedLocationsController(ISavedLocationStore store)
    {
        _store = store;
    }

    [HttpGet]
    [RequireCapability(Capabilities.ProfileReadSelf)]
    [ProducesResponseType(typeof(SavedLocationsListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        var items = await _store.ListAsync(userId, ct);
        return Ok(ToListResponse(userId, items));
    }

    [HttpGet("{id}")]
    [RequireCapability(Capabilities.ProfileReadSelf)]
    [ProducesResponseType(typeof(SavedLocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        var found = await _store.GetAsync(userId, id, ct);
        return found is null ? NotFoundProblem(id) : Ok(ToResponse(found));
    }

    [HttpPost]
    [RequireCapability(Capabilities.ProfileWriteSelf)]
    [ProducesResponseType(typeof(SavedLocationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateSavedLocationRequest body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (body is null)
            return BadRequest(new ProblemDetails { Title = "Request body is required.", Status = StatusCodes.Status400BadRequest });

        var created = await _store.CreateAsync(userId, body, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ToResponse(created));
    }

    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    [RequireCapability(Capabilities.ProfileWriteSelf)]
    [ProducesResponseType(typeof(SavedLocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateSavedLocationRequest body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (body is null)
            return BadRequest(new ProblemDetails { Title = "Request body is required.", Status = StatusCodes.Status400BadRequest });

        var updated = await _store.UpdateAsync(userId, id, body, ct);
        return updated is null ? NotFoundProblem(id) : Ok(ToResponse(updated));
    }

    [HttpDelete("{id}")]
    [RequireCapability(Capabilities.ProfileWriteSelf)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        var removed = await _store.DeleteAsync(userId, id, ct);
        return removed ? NoContent() : NotFoundProblem(id);
    }

    private bool TryGetUserId(out string userId, out IActionResult problem)
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            problem = null!;
            return true;
        }
        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            problem = null!;
            return true;
        }
        userId = string.Empty;
        problem = Unauthorized();
        return false;
    }

    private NotFoundObjectResult NotFoundProblem(string id) => NotFound(new ProblemDetails
    {
        Title = "Saved location not found.",
        Detail = $"No saved location with id '{id}' exists for the current user.",
        Status = StatusCodes.Status404NotFound
    });

    private static SavedLocationsListResponse ToListResponse(string userId, IReadOnlyList<SavedLocation> items) => new()
    {
        UserId = userId,
        Items = items.Select(ToResponse).ToList(),
        DefaultId = items.FirstOrDefault(l => l.IsDefault)?.Id
    };

    private static SavedLocationResponse ToResponse(SavedLocation l) => new()
    {
        Id = l.Id,
        UserId = l.UserId,
        Label = l.Label,
        Address = l.Address,
        Latitude = l.Latitude,
        Longitude = l.Longitude,
        IsDefault = l.IsDefault,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt
    };
}
