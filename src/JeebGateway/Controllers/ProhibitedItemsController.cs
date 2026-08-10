using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Mobile-facing read of the active prohibited-items catalog plus per-user
/// acknowledgment ledger. The first-request acknowledgment flow is:
///   1. mobile GETs /prohibited-items to render the warning sheet
///   2. user taps "I understand"
///   3. mobile POSTs /prohibited-items/acknowledge with the version echoed back
///
/// The version is the ban-service-owned opaque immutable catalog tag. The
/// gateway never parses it or substitutes ban-service's numeric CAS revision.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("prohibited-items")]
// ADR-005 L2 §H–J participant {client, jeeber}: BOTH actions resolve the caller (the LIST returns the
// caller's per-user acknowledgment state, so it is an identified-participant read today, NOT anonymous;
// the catalog itself is public but this coupled read+ack endpoint requires a caller). Preserves the
// existing identified-caller behaviour; ack-version legality stays STATE in-action.
[RequireCapability(Capabilities.ProhibitedAck)]
public class ProhibitedItemsController : ControllerBase
{
    private readonly IProhibitedItemsStore _store;

    public ProhibitedItemsController(IProhibitedItemsStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ProhibitedItemsListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var catalog = await _store.GetActiveCatalogAsync(ct);
        var items = catalog.Items;
        var version = catalog.Version;
        var ack = await _store.GetAcknowledgmentAsync(userId, version, ct);
        var acknowledged = ack is not null && string.Equals(ack.Version, version, StringComparison.Ordinal);

        return Ok(new ProhibitedItemsListResponse
        {
            Items = items.Select(ToDto).ToList(),
            Version = version,
            Acknowledged = acknowledged,
            AcknowledgedAt = acknowledged ? ack!.AcknowledgedAt : null
        });
    }

    [HttpPost("acknowledge")]
    [ProducesResponseType(typeof(ProhibitedItemsAcknowledgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Acknowledge(
        [FromBody] ProhibitedItemsAcknowledgeRequest body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Version))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "version is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var current = await _store.GetActiveCatalogAsync(ct);
        var currentVersion = current.Version;

        if (!string.Equals(body.Version, currentVersion, StringComparison.Ordinal))
        {
            return Conflict(ListChangedProblem(currentVersion, body.Version));
        }

        UserAcknowledgment ack;
        try
        {
            // ban-service compares the supplied immutable tag with the current
            // tag atomically with this write. The pre-read above is only an
            // early client-friendly rejection, not the concurrency guard.
            ack = await _store.AcknowledgeAsync(userId, currentVersion, ct);
        }
        catch (StaleProhibitedCatalogVersionException)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The prohibited-items list has changed; re-fetch and acknowledge again.",
                Detail = $"Version '{body.Version}' is no longer current.",
                Status = StatusCodes.Status409Conflict
            });
        }
        catch (ProhibitedCatalogConflictException)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The prohibited-items list changed while it was being acknowledged; re-fetch and try again.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return Ok(new ProhibitedItemsAcknowledgeResponse
        {
            UserId = ack.UserId,
            Version = ack.Version,
            AcknowledgedAt = ack.AcknowledgedAt
        });
    }

    private static ProblemDetails ListChangedProblem(
        string currentVersion,
        string? suppliedVersion) => new()
    {
        Title = "The prohibited-items list has changed; re-fetch and acknowledge again.",
        Detail = $"Expected version '{currentVersion}', got '{suppliedVersion}'.",
        Status = StatusCodes.Status409Conflict
    };

    private static ProhibitedItemDto ToDto(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Severity = i.Severity.ToString().ToLowerInvariant(),
        Active = i.Active,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt
    };
}
