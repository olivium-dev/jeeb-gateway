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
/// The version is the max updated_at across the active set, so reactivating or
/// editing an item bumps it and the user must acknowledge again.
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

        var items = await _store.ListActiveAsync(ct);
        var version = ComputeVersion(items);
        var ack = await _store.GetAcknowledgmentAsync(userId, ct);
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

        var current = await _store.ListActiveAsync(ct);
        var currentVersion = ComputeVersion(current);

        if (!string.Equals(body.Version, currentVersion, StringComparison.Ordinal))
        {
            return Conflict(new ProblemDetails
            {
                Title = "The prohibited-items list has changed; re-fetch and acknowledge again.",
                Detail = $"Expected version '{currentVersion}', got '{body.Version}'.",
                Status = StatusCodes.Status409Conflict
            });
        }

        var ack = await _store.AcknowledgeAsync(userId, currentVersion, ct);
        return Ok(new ProhibitedItemsAcknowledgeResponse
        {
            UserId = ack.UserId,
            Version = ack.Version,
            AcknowledgedAt = ack.AcknowledgedAt
        });
    }

    private static string ComputeVersion(IReadOnlyList<ProhibitedItem> items)
    {
        if (items.Count == 0) return "empty";

        // Round-trip "O" is invariant and millisecond-stable; using the max
        // UpdatedAt lets clients treat the value as opaque while still giving
        // operators a human-readable signal during debugging.
        var max = items.Max(i => i.UpdatedAt);
        return max.ToUniversalTime().ToString("O");
    }

    private static ProhibitedItemDto ToDto(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Active = i.Active,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt
    };
}
