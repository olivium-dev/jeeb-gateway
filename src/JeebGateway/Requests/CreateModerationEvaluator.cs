using JeebGateway.Controllers;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// JEBV4-212 (E17): the single create-time prohibited-items moderation gate, shared
/// verbatim by the legacy <see cref="JeebGateway.Controllers.RequestsController"/> create
/// path and the V1 <see cref="JeebGateway.Controllers.V1.JeebRequestsController"/> create
/// path. Previously the gate lived as a private method on the legacy controller only, so a
/// prohibited item posted to <c>POST /v1/requests</c> (the route the mobile app actually
/// uses) was NOT blocked. Extracting the orchestration here guarantees both routes enforce
/// identical block/warn/fail-closed semantics and can never drift.
///
/// <para>GR-3: the gate is a stateless check — it composes the gateway-owned
/// <see cref="IProhibitedItemScanner"/> over the gateway-owned lexicon and the existing
/// per-user ack ledger. It persists NO new moderation record and introduces NO new gateway
/// store seam.</para>
/// </summary>
public sealed class CreateModerationEvaluator
{
    private readonly IProhibitedItemScanner _scanner;
    private readonly IProhibitedItemsStore _prohibited;
    private readonly CreateModerationOptions _moderation;
    private readonly ILogger<CreateModerationEvaluator> _logger;

    public CreateModerationEvaluator(
        IProhibitedItemScanner scanner,
        IProhibitedItemsStore prohibited,
        IOptions<CreateModerationOptions> moderation,
        ILogger<CreateModerationEvaluator> logger)
    {
        _scanner = scanner;
        _prohibited = prohibited;
        _moderation = moderation.Value;
        _logger = logger;
    }

    /// <summary>
    /// JEB-63 (S05 N1 / A1.1) create-time moderation gate. Returns:
    ///   <list type="bullet">
    ///     <item><c>null</c> — allowed (gate off, no review-grade match, or warn already
    ///       acknowledged): the create proceeds.</item>
    ///     <item>503 <c>moderation_unavailable</c> — lexicon empty or unloadable
    ///       (FT-04 fail-closed).</item>
    ///     <item>409 <c>prohibited_item_blocked</c> — a block-severity match; an ack does
    ///       NOT override it (AC7).</item>
    ///     <item>409 <c>prohibited_item_requires_ack</c> — a warn-severity match and the
    ///       caller has not acknowledged the current lexicon version.</item>
    ///   </list>
    /// No-op (returns null) when the gate flag is OFF.
    /// </summary>
    public async Task<IActionResult?> EvaluateAsync(string clientId, string? description, CancellationToken ct)
    {
        if (!_moderation.Enabled) return null;

        // WS-06 fail-closed: if the lexicon cannot be loaded (0 active items) we must NOT
        // allow the request through silently. A 503 is surfaced so callers know the
        // moderation service is temporarily unavailable and can retry. The load +
        // fail-closed + scan + version logic is shared with the standalone
        // POST /moderation/jeeb/check endpoint via ModerationGate so the paths cannot drift.
        ModerationGateOutcome outcome;
        try
        {
            outcome = await new ModerationGate(_prohibited, _scanner).EvaluateAsync(description, ct);
        }
        catch (LexiconUnavailableException ex)
        {
            _logger.LogError(ex, "Prohibited-items lexicon unavailable while moderation gate is enabled; failing closed with 503.");
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Moderation service temporarily unavailable",
                Detail = ex.Message
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }

        var severity = outcome.GatingSeverity;
        if (severity is null) return null;

        var matchDtos = outcome.Scan.Matches
            .Select(m => new ModerationMatchDto(m.ItemName, m.Category, m.Severity.ToString().ToLowerInvariant()))
            .ToList();

        if (severity == ProhibitedSeverity.Block)
        {
            // AC1 / AC7: block is a hard reject; prohibited_ack must NOT override.
            return new ConflictObjectResult(new ProhibitedItemProblemDetails
            {
                Title = "This request contains a prohibited item and cannot be created.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/prohibited-item-blocked",
                Reason = "prohibited_item_blocked",
                Matches = matchDtos
            });
        }

        // Warn severity: allowed only once the caller has acknowledged the CURRENT lexicon
        // version (same version semantics as GET /prohibited-items + the acknowledge route).
        var currentVersion = outcome.Version;
        var ack = await _prohibited.GetAcknowledgmentAsync(clientId, ct);
        var acknowledged = ack is not null && string.Equals(ack.Version, currentVersion, StringComparison.Ordinal);

        if (acknowledged) return null;

        return new ConflictObjectResult(new ProhibitedItemProblemDetails
        {
            Title = "This request contains an item that requires acknowledgment before it can be created.",
            Status = StatusCodes.Status409Conflict,
            Type = "https://jeeb.dev/errors/prohibited-item-requires-ack",
            Reason = "prohibited_item_requires_ack",
            Matches = matchDtos
        });
    }
}
