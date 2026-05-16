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
    private readonly IDataExportStore _store;
    private readonly TimeProvider _clock;

    public DataExportController(IDataExportStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    [HttpPost]
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

        var record = await _store.RequestAsync(userId, format, ct);
        return StatusCode(StatusCodes.Status202Accepted, ToResponse(record));
    }

    [HttpGet]
    [ProducesResponseType(typeof(DataExportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var record = await _store.GetLatestForUserAsync(userId, ct);
        if (record is null) return NotFound();
        return Ok(ToResponse(record));
    }

    [HttpGet("{token}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string token, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var record = await _store.GetByDownloadTokenAsync(token, now, ct);
        if (record is null || record.Payload is null)
        {
            return NotFound();
        }

        var bytes = record.Payload;
        var contentType = record.PayloadContentType ?? "application/octet-stream";
        var fileName = $"jeeb-data-export-{record.Id}.{(record.Format == DataExportFormat.Pdf ? "pdf" : "json")}";

        // Mark delivered AFTER capturing the bytes — MarkDelivered clears
        // the payload so a second download attempt returns 404 (the link
        // is single-use). Capture-before-clear keeps the response intact.
        await _store.MarkDeliveredAsync(record.Id, now, ct);

        return File(bytes, contentType, fileName);
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
}
