using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.JeebSupport;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Controllers;

/// <summary>
/// The Jeeb SUPPORT surface the mobile app consumes (<c>DioSupportRepository</c>,
/// JM-063) — the contact-us / help-ticket routes:
///
/// <list type="bullet">
///   <item><c>POST /v1/support/tickets</c> — create a ticket (the only path mobile calls today).</item>
///   <item><c>GET  /v1/support/tickets/{id}</c> — read one (owner-scoped).</item>
///   <item><c>GET  /v1/support/tickets</c> — list the caller's tickets (paged).</item>
///   <item><c>GET  /v1/support/categories</c> — static gateway-owned catalog (no state).</item>
/// </list>
///
/// <para>
/// ADR-0001 + <b>ADR-0005</b> (STATELESS &amp; THIN): this controller authenticates,
/// resolves the caller's own id from the bearer token (or trusted-edge <c>X-User-Id</c>),
/// reconciles the mobile DTO drift, and persists/reads tickets via
/// <b>jeeb-state-service</b> (<see cref="IJeebSupportTicketStore"/> over the opaque KV) —
/// NOT an in-gateway in-memory store. It holds NO state itself. The Jeeb shaping lives in
/// the pure <see cref="JeebSupportProjection"/>. Sibling of
/// <see cref="JeebNotificationsInboxController"/>; mirrors the wallet/reviews/notifications
/// families (PR #196/#197/#198).
/// </para>
///
/// <para><b>DTO-drift fixes (mobile-facing shape authoritative).</b> The create route accepts
/// the mobile <c>orderRef</c> AND the canonical <c>orderId</c> (reconciled to one stored
/// <c>orderId</c>), and the mobile category enum names <c>delivery</c>/<c>kycAppeal</c> (mapped
/// to the canonical <c>order</c>/<c>kyc</c>) — so mobile's live payload is accepted, never 400'd
/// on a name mismatch. See <see cref="JeebSupportProjection.ReconcileCategory"/> /
/// <see cref="JeebSupportProjection.BuildRow"/>.</para>
///
/// <para><b>list-by-owner coverage gap (ADR-0005).</b> jeeb-state-service's opaque KV is
/// GET-by-key only (no list-by-owner / prefix scan), so <see cref="ListTickets"/> serves the
/// cold-start empty page until that generic primitive ships upstream — rather than fabricating
/// an in-gateway index (which would violate ADR-0001/0005). Mobile does not call list today.</para>
/// </summary>
[ApiController]
[Route("v1/support")]
[Produces("application/json")]
public sealed class JeebSupportController : ControllerBase
{
    private readonly IJeebSupportTicketStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<JeebSupportController> _log;

    public JeebSupportController(
        IJeebSupportTicketStore store,
        TimeProvider clock,
        ILogger<JeebSupportController> log)
    {
        _store = store;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// POST /v1/support/tickets — create a support ticket (JM-063). The authoritative owner
    /// is the bearer/edge identity. Returns 201 with the canonical ticket DTO; the mobile
    /// <c>DioSupportRepository</c> reads <c>id</c> + <c>status</c> off it.
    /// </summary>
    [HttpPost("tickets")]
    [RequireCapability(Capabilities.SupportCreateSelf)] // ADR-005 §B any-auth; owner = caller
    [ProducesResponseType(typeof(SupportTicketResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateTicket(
        [FromBody] CreateSupportTicketRequest? request,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var ownerId, out var unauthorized)) return unauthorized;

        if (request is null || string.IsNullOrWhiteSpace(request.Body))
        {
            return BadRequest(Problem400("A non-empty ticket body is required."));
        }

        if (!JeebSupportProjection.IsKnownCategory(request.Category))
        {
            return BadRequest(Problem400(
                $"Unknown support category '{request.Category}'. See GET /v1/support/categories."));
        }

        var id = Guid.NewGuid().ToString("N");
        var row = JeebSupportProjection.BuildRow(id, ownerId, request, _clock.GetUtcNow());

        try
        {
            await _store.CreateAsync(row, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpstreamProblem(ex);
        }

        var dto = JeebSupportProjection.ProjectTicket(row);
        return CreatedAtAction(nameof(GetTicket), new { id = row.Id }, dto);
    }

    /// <summary>
    /// GET /v1/support/tickets/{id} — read one of the caller's tickets. 404 when it does not
    /// exist OR is not owned by the caller (owner-scoping is STATE, enforced here).
    /// </summary>
    [HttpGet("tickets/{id}")]
    [RequireCapability(Capabilities.SupportReadOwn)] // ADR-005 §B; own-vs-any = STATE (below)
    [ProducesResponseType(typeof(SupportTicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetTicket(string id, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(id)) return NotFound();

        SupportTicketRow? row;
        try
        {
            row = await _store.GetAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpstreamProblem(ex);
        }

        // STATE: the caller may only read their OWN ticket; a foreign/unknown id is a 404
        // (never leak existence of another user's ticket).
        if (row is null || !string.Equals(row.UserId, callerId, StringComparison.Ordinal))
        {
            return NotFound();
        }

        return Ok(JeebSupportProjection.ProjectTicket(row));
    }

    /// <summary>
    /// GET /v1/support/tickets?page=&amp;pageSize= — one page of the caller's own tickets,
    /// newest-first. See the controller remarks: serves the cold-start empty page until
    /// jeeb-state-service ships a list-by-owner primitive (ADR-0005).
    /// </summary>
    [HttpGet("tickets")]
    [RequireCapability(Capabilities.SupportReadOwn)] // ADR-005 §B; caller's own rows only
    [ProducesResponseType(typeof(SupportTicketsPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListTickets(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        try
        {
            var rows = await _store.ListByOwnerAsync(callerId, ct);
            return Ok(JeebSupportProjection.ProjectPage(rows, page, pageSize));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpstreamProblem(ex);
        }
    }

    /// <summary>
    /// GET /v1/support/categories — the static, gateway-owned category catalog the picker
    /// renders. No upstream call, no state (ADR-0005: categories are a constant).
    /// </summary>
    [HttpGet("categories")]
    [RequireCapability(Capabilities.SupportReadOwn)] // any authenticated Jeeb user
    [ProducesResponseType(typeof(SupportCategoriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult ListCategories()
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        return Ok(JeebSupportProjection.ProjectCategories());
    }

    private static ProblemDetails Problem400(string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest,
        Type = "https://jeeb.dev/errors/invalid-support-ticket",
    };

    /// <summary>
    /// JEBV4-249 — map a jeeb-state-service store failure to a sanitized RFC 7807
    /// ProblemDetails. The store exception (which may wrap connection / driver detail
    /// or an upstream body) is logged server-side ONLY and never echoed to the caller;
    /// the client always sees a generic 502 Bad Gateway. (Was
    /// the raw <c>ex.Message</c> in the response detail at the create/read/list store
    /// catches — an information-disclosure leak.)
    /// </summary>
    private IActionResult UpstreamProblem(Exception ex)
    {
        _log.LogWarning(ex,
            "Support BFF: jeeb-state-service call failed on {Method} {Path}.",
            Request.Method, Request.Path);

        return Problem(
            title: "The support request could not be completed.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}
