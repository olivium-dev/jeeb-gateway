using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.JeebSupport;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("v1/support")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("support")]
[Produces("application/json")]
public sealed class JeebSupportController : CaseControllerBase
{
    private readonly IGenericCaseGatewayService _cases;

    public JeebSupportController(IGenericCaseGatewayService cases) => _cases = cases;

    [HttpPost("tickets")]
    [RequireCapability(Capabilities.SupportCreateSelf)]
    public async Task<IActionResult> CreateTicket(
        [FromBody] CreateSupportTicketRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            if (request is null) throw new CaseValidationException("request body is required.");
            var category = JeebSupportProjection.ReconcileCategory(request.Category)
                ?? throw new CaseValidationException($"Unknown support category '{request.Category}'.");
            var orderId = First(request.OrderId, request.OrderRef);
            var attachments = (request.Attachments ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal).ToArray();
            var key = RequireIdempotencyKey(idempotencyKey);
            var row = await _cases.CreateSupportAsync(new CreateSupportCaseInput
            {
                UserId = userId,
                UserRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext),
                Category = category,
                Subject = request.Subject,
                Body = request.Body ?? string.Empty,
                OrderId = orderId,
                Attachments = attachments,
                IdempotencyKey = key,
            }, ct);
            Response.Headers.ETag = $"\"{row.Case.Version}\"";
            return CreatedAtAction(nameof(GetTicket), new { id = row.Case.CaseId }, CaseApiProjection.Project(row, false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Support, "create"); }
    }

    [HttpGet("tickets/{id}")]
    [RequireCapability(Capabilities.SupportReadOwn)]
    public async Task<IActionResult> GetTicket(string id, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            var detail = await _cases.GetForUserAsync(id, userId, UserIdentity.IsAdmin(HttpContext), ct);
            if (!string.Equals(detail.Case.Kind, GenericCaseKinds.Support, StringComparison.Ordinal)) return NotFound();
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, UserIdentity.IsAdmin(HttpContext)));
        }
        catch (CaseAccessDeniedException) { return NotFound(); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Support, "get"); }
    }

    [HttpGet("tickets")]
    [RequireCapability(Capabilities.SupportReadOwn)]
    public async Task<IActionResult> ListTickets([FromQuery] string? status, [FromQuery] string? cursor,
        [FromQuery] int pageSize = 20, [FromQuery] int? limit = null, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            var page = await _cases.ListForUserAsync(GenericCaseKinds.Support, userId,
                new GenericCaseQueryV1
                {
                    Status = status, Limit = Math.Clamp(limit ?? pageSize, 1, 200), Cursor = cursor,
                }, ct);
            return Ok(CaseApiProjection.Project(page));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Support, "list"); }
    }

    [HttpGet("tickets/{id}/messages")]
    [RequireCapability(Capabilities.SupportReadOwn)]
    public async Task<IActionResult> ListMessages(string id, [FromQuery] string? cursor,
        [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            var page = await _cases.ListMessagesForUserAsync(
                id, userId, false, Math.Clamp(limit, 1, 200), cursor, ct);
            return Ok(new CaseMessagePageResponseV2
            {
                Items = page.Items,
                Total = page.Items.Count,
                NextCursor = page.NextCursor,
            });
        }
        catch (CaseAccessDeniedException) { return NotFound(); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Support, "messages"); }
    }

    [HttpPost("tickets/{id}/reply")]
    [RequireCapability(Capabilities.SupportReadOwn)]
    public async Task<IActionResult> Reply(string id, [FromBody] CaseReplyRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Body)
                && (request.Attachments is null || request.Attachments.Count == 0))
                throw new CaseValidationException("A reply requires a body or attachment.");
            var existing = await _cases.GetForUserAsync(id, userId, false, ct);
            if (!string.Equals(existing.Case.Kind, GenericCaseKinds.Support, StringComparison.Ordinal)) return NotFound();
            var detail = await _cases.AddMessageAsync(id, checked((int)RequireVersion(request.ExpectedVersion)),
                request.ReplyToId is null ? "message" : "reply", userId,
                CanonicalDeliveryVocab.ActorRoleFor(HttpContext), RequireIdempotencyKey(idempotencyKey),
                request.Body, request.ReplyToId, request.Attachments, ct);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, false));
        }
        catch (CaseAccessDeniedException) { return NotFound(); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Support, "reply"); }
    }

    [HttpGet("categories")]
    [RequireCapability(Capabilities.SupportReadOwn)]
    public IActionResult ListCategories() => Ok(JeebSupportProjection.ProjectCategories());

    private static string? First(params string?[] values) => values
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
