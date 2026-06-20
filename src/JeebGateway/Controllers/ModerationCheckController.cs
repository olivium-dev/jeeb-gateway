using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// WS-06 (RAT-04, ACCT-03, ADM-03): the synchronous content-moderation check
/// <c>POST /moderation/jeeb/check</c>. It runs arbitrary text through the same
/// prohibited-items lexicon scan that gates a request-create, but is content-agnostic:
/// callers use it to moderate a display name (ACCT-03), pre-flight a request description
/// (RAT-04), or validate admin input (ADM-03) without creating any row.
///
/// The verdict mirrors the create-gate severity mapping exactly so a caller can predict
/// the create outcome:
///   • <c>block</c> ⇒ the create path returns 409 <c>prohibited_item_blocked</c>;
///   • <c>warn</c>  ⇒ the create path returns 409 <c>prohibited_item_requires_ack</c> until the
///     caller acknowledges <c>Version</c>;
///   • <c>allow</c> ⇒ the text is clean.
///
/// Fail-closed: when the lexicon is empty or unloadable the endpoint returns 503 rather
/// than reporting a misleading <c>allow</c> — identical to the request-create gate (JEB-1504).
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("moderation/jeeb")]
public class ModerationCheckController : ControllerBase
{
    private const int MaxTextLength = 4000;

    private readonly ModerationGate _gate;

    public ModerationCheckController(IProhibitedItemsStore store, IProhibitedItemScanner scanner)
    {
        // ModerationGate is a thin, stateless composition of the already-registered
        // store + scanner, so it is constructed here rather than via a Program.cs DI line
        // (WS-06 keeps off the hot files; the create-gate composes the same two services).
        _gate = new ModerationGate(store, scanner);
    }

    [HttpPost("check")]
    // ADR-005 L2 §H–J participant {client, jeeber}: pre-submission / display-name moderation check.
    [RequireCapability(Capabilities.ProhibitedScan)]
    [ProducesResponseType(typeof(ModerationCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Check([FromBody] ModerationCheckRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Text))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "text is required and cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Text.Length > MaxTextLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"text must be {MaxTextLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        ModerationGateOutcome outcome;
        try
        {
            outcome = await _gate.EvaluateAsync(body.Text, ct);
        }
        catch (LexiconUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Moderation service temporarily unavailable",
                Detail = ex.Message
            });
        }

        var matches = outcome.Scan.Matches
            .Select(m => new ModerationMatchDto(m.ItemName, m.Category, m.Severity.ToString().ToLowerInvariant()))
            .ToList();

        var (decision, reason) = outcome.GatingSeverity switch
        {
            ProhibitedSeverity.Block => ("block", "prohibited_item_blocked"),
            ProhibitedSeverity.Warn => ("warn", "prohibited_item_requires_ack"),
            _ => ("allow", (string?)null)
        };

        return Ok(new ModerationCheckResponse
        {
            Decision = decision,
            Version = outcome.Version,
            Reason = reason,
            Matches = matches
        });
    }
}
