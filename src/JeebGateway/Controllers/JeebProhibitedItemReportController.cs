using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// The user-facing "report a prohibited item on a request/delivery" surface the
/// mobile app consumes. Unlike <c>POST /prohibited-items/scan</c> — which is an
/// advisory NLP screen that only records a flag when the catalog matches the
/// description text — this route lets an authenticated participant DIRECTLY
/// report that a specific request/delivery contains a prohibited item, in their
/// own words, regardless of whether the lexicon happens to match.
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): this controller authenticates, resolves the
/// caller's own id from the bearer token (or trusted-edge <c>X-User-Id</c>), and
/// persists the report by REUSING the existing moderation backbone —
/// <see cref="IFlaggedRequestStore"/> — so a user report lands in the very same
/// admin review queue (<c>GET /admin/prohibited-items/flagged</c> +
/// <c>.../{id}/decision</c>) that the scanner-produced flags use. No new store,
/// no sibling moderation service: the FlaggedRequest model already carries
/// RequestId + UserId + Description + Status(Pending) and the clear/uphold flow.
/// A user report carries an empty <c>Matches</c> list (no scanner hit) — admins
/// triage it from the free-text description.
/// </para>
///
/// <para>
/// Capability: <see cref="Capabilities.ProhibitedScan"/> (prohibited.scan) — the
/// {client, jeeber} participant capability already used by the scan route, so
/// both delivery parties can report. The route lives on the canonical
/// <c>/v1/jeeb/*</c> BFF surface alongside the wallet/reviews/support families.
/// </para>
/// </summary>
[ApiController]
[Route("v1/jeeb/prohibited-items")]
[Produces("application/json")]
public sealed class JeebProhibitedItemReportController : ControllerBase
{
    private const int MaxReasonLength = 4000;

    private readonly IFlaggedRequestStore _store;

    public JeebProhibitedItemReportController(IFlaggedRequestStore store)
    {
        _store = store;
    }

    /// <summary>
    /// POST /v1/jeeb/prohibited-items/report — report a prohibited item on a
    /// request/delivery. The authoritative reporter is the bearer/edge identity.
    /// Returns 201 with the created report (a FlaggedRequest in <c>pending</c>
    /// status) so the mobile client can show a confirmation / reference id.
    /// </summary>
    [HttpPost("report")]
    [RequireCapability(Capabilities.ProhibitedScan)] // {client, jeeber} participant report
    [ProducesResponseType(typeof(ProhibitedItemReportResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Report(
        [FromBody] ProhibitedItemReportRequest? body,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Reason))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "reason is required and cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Reason.Length > MaxReasonLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"reason must be {MaxReasonLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(body.RequestId) && string.IsNullOrWhiteSpace(body.DeliveryId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "either requestId or deliveryId is required to anchor the report.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Anchor the report to a request/delivery ref. FlaggedRequest.RequestId is
        // the moderation-queue anchor; deliveries and requests share this single
        // ref field (admins resolve the underlying entity from it).
        var anchorRef = string.IsNullOrWhiteSpace(body.RequestId) ? body.DeliveryId : body.RequestId;

        var flagged = await _store.CreateAsync(new FlaggedRequestCreate
        {
            RequestId = anchorRef,
            UserId = userId,
            Description = body.Reason.Trim(),
            // A user report has no NLP scanner hit. Empty list = "reported by a
            // human", which the admin queue triages from the free-text reason.
            Matches = Array.Empty<ProhibitedItemMatch>()
        }, ct);

        var response = new ProhibitedItemReportResponse
        {
            Id = flagged.Id,
            RequestId = flagged.RequestId,
            ReporterUserId = flagged.UserId,
            Reason = flagged.Description,
            Status = flagged.Status.ToString().ToLowerInvariant(),
            CreatedAt = flagged.CreatedAt
        };

        return Created($"/v1/jeeb/prohibited-items/report/{flagged.Id}", response);
    }
}

/// <summary>Mobile request body for reporting a prohibited item on a request/delivery.</summary>
public sealed class ProhibitedItemReportRequest
{
    /// <summary>The request the reported item belongs to. One of requestId/deliveryId is required.</summary>
    public string? RequestId { get; set; }

    /// <summary>The delivery the reported item belongs to. One of requestId/deliveryId is required.</summary>
    public string? DeliveryId { get; set; }

    /// <summary>Free-text reason / description of the prohibited item (required).</summary>
    public string? Reason { get; set; }
}

/// <summary>Confirmation returned to the mobile client after a report is filed.</summary>
public sealed class ProhibitedItemReportResponse
{
    public required string Id { get; init; }
    public required string? RequestId { get; init; }
    public required string ReporterUserId { get; init; }
    public required string Reason { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
