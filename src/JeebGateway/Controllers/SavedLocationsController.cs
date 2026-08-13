using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        try
        {
            // An EMPTY answered list stays a 200: a user with nothing saved yet is not an error.
            var items = await _store.ListAsync(userId, ct);
            return Ok(ToListResponse(userId, items));
        }
        catch (UserPreferencesUnavailableException)
        {
            return UpstreamUnavailableProblem();
        }
    }

    [HttpGet("{id}")]
    [RequireCapability(Capabilities.ProfileReadSelf)]
    [ProducesResponseType(typeof(SavedLocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        try
        {
            var found = await _store.GetAsync(userId, id, ct);
            return found is null ? NotFoundProblem(id) : Ok(ToResponse(found));
        }
        catch (UserPreferencesUnavailableException)
        {
            // Without an answer we cannot know the id is absent; a 404 would be a guess.
            return UpstreamUnavailableProblem();
        }
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

        try
        {
            var created = await _store.CreateAsync(userId, body, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, ToResponse(created));
        }
        catch (TimeoutException)
        {
            return UpstreamTimeoutProblem();
        }
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

        try
        {
            var updated = await _store.UpdateAsync(userId, id, body, ct);
            return updated is null ? NotFoundProblem(id) : Ok(ToResponse(updated));
        }
        catch (TimeoutException)
        {
            return UpstreamTimeoutProblem();
        }
    }

    [HttpDelete("{id}")]
    [RequireCapability(Capabilities.ProfileWriteSelf)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (!Guid.TryParseExact(id, "N", out _)) return NotFoundProblem(id);

        try
        {
            var removed = await _store.DeleteAsync(userId, id, ct);
            return removed ? NoContent() : NotFoundProblem(id);
        }
        catch (TimeoutException)
        {
            return UpstreamTimeoutProblem();
        }
    }

    // O10 precedent (wallet ledger): a read the store could NOT answer is a retryable
    // 502 — never an authoritative empty 200 that reads as "you saved nothing".
    private ObjectResult UpstreamUnavailableProblem() => Problem(
        title: "Saved locations are temporarily unavailable.",
        detail: "The preferences service did not answer. Please try again.",
        statusCode: StatusCodes.Status502BadGateway,
        type: "https://jeeb.dev/errors/dependency-unavailable");

    // Mirrors NotificationPreferencesController.Patch (JEBV4-30): an aborted write
    // pre-read is a fast 504, not the opaque 500 the unhandled TimeoutException produced.
    private ObjectResult UpstreamTimeoutProblem() => Problem(
        title: "Saved locations service timed out.",
        detail: "The preferences service did not respond in time. Please try again.",
        statusCode: StatusCodes.Status504GatewayTimeout,
        type: "https://jeeb.dev/errors/upstream-timeout");

    // SEC-C1 (Leg-11): identity derives from the validated JWT principal; the raw
    // X-User-Id header is honoured ONLY when EdgeIdentityTrust permits it (Dev/Testing or
    // a secret-gated trusted edge). Delegating to the shared, gated UserIdentity closes the
    // spoof/IDOR — a raw client can no longer scope reads/writes to another user's data.
    private bool TryGetUserId(out string userId, out IActionResult problem)
        => UserIdentity.TryGetUserId(HttpContext, out userId, out problem);

    private ObjectResult NotFoundProblem(string id) => Problem(
        title: "Saved location not found.",
        detail: $"No saved location with id '{id}' exists for the current user.",
        statusCode: StatusCodes.Status404NotFound);

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
