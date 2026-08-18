using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-042: GDPR-like right of access. POST queues a full export
/// (profile, orders, ratings, chat history) with a 72-hour SLA; once the
/// processor finishes the user is notified out-of-band (email + push)
/// with a secure download link. The link is single-tenant and time-boxed.
///
/// The controller intentionally never serves the payload bytes by user
/// id — only the unguessable token does. That way leaking the export
/// requires leaking the token, not just compromising the session.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("users/me/data-export")]
public class DataExportController : ControllerBase
{
    private readonly IDataExportWorkflow _workflow;

    public DataExportController(IDataExportWorkflow workflow)
    {
        _workflow = workflow;
    }

    [HttpPost]
    // ADR-005 L2 §B self / any-authenticated data export.
    [RequireCapability(Capabilities.DataExportSelf)]
    [ProducesResponseType(typeof(DataExportResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestExport([FromBody] DataExportRequestBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var format = string.IsNullOrWhiteSpace(body?.Format)
            ? DataExportFormat.Json
            : body!.Format!.ToLowerInvariant();

        if (!DataExportFormat.All.Contains(format))
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"format must be one of: {string.Join(", ", DataExportFormat.All)}",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var record = await _workflow.RequestAsync(userId, format, ct);
            return StatusCode(StatusCodes.Status202Accepted, ToResponse(record));
        }
        catch (DataExportDisabledException)
        {
            return Disabled();
        }
    }

    [HttpGet]
    // ADR-005 L2 §B self / any-authenticated (STATE: caller-scoped latest stays in-action).
    [RequireCapability(Capabilities.DataExportSelf)]
    [ProducesResponseType(typeof(DataExportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        try
        {
            var record = await _workflow.GetLatestForUserAsync(userId, ct);
            if (record is null) return NotFound();
            return Ok(ToResponse(record));
        }
        catch (DataExportDisabledException)
        {
            return Disabled();
        }
    }

    // ADR-004 D1: public by design — the unguessable single-use download token IS the
    // credential (capability URL); it is presented anonymously by the link recipient, not
    // via a session bearer. Create (POST) and status (GET) above stay session-authed.
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    // ADR-005 L2: public by design — the unguessable single-use token IS the credential (capability
    // URL), presented anonymously. [PublicEndpoint] opts out of L2 and satisfies the default-deny guard;
    // [AllowAnonymous] opts out of L1 (matches the existing ADR-004 D1 decision above).
    [PublicEndpoint("Capability-URL data-export download — token is the credential (ADR-004 D1).")]
    [HttpGet("{token}/download")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string token, CancellationToken ct)
    {
        try
        {
            var download = await _workflow.RedeemDownloadAsync(token, ct);
            if (download is null)
            {
                return NotFound();
            }
            // The gateway never re-streams export bytes. The capability was
            // atomically consumed in state-service before this redirect; the
            // artifact owner minted a short-lived, private, single-use GET URL.
            return Redirect(download.AbsoluteUri);
        }
        catch (DataExportDisabledException)
        {
            return Disabled();
        }
    }

    private DataExportResponse ToResponse(DataExportRequest r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Status = r.Status,
        Format = r.Format,
        RequestedAt = r.RequestedAt,
        DueBy = r.DueBy,
        ReadyAt = r.ReadyAt,
        LinkExpiresAt = r.LinkExpiresAt,
        DownloadUrl = r.Status == DataExportStatus.Ready && r.DownloadToken is not null
            ? $"/users/me/data-export/{r.DownloadToken}/download"
            : null,
        PayloadSizeBytes = r.PayloadSizeBytes
    };

    private ObjectResult Disabled() => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Data export unavailable",
        detail: "Data export is disabled in this environment until a compatible private artifact owner is configured.");
}
