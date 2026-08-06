using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Disputes.V2;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers.V1;

[ApiController]
public sealed class DisputeCasesController : CaseControllerBase
{
    private readonly IGenericCaseGatewayService _cases;
    private readonly IDisputeCaseService? _legacyCases;
    private readonly IAdminAuditLog? _auditLog;

    public DisputeCasesController(
        IGenericCaseGatewayService cases,
        IDisputeCaseService? legacyCases = null,
        IAdminAuditLog? auditLog = null)
    {
        _cases = cases;
        _legacyCases = legacyCases;
        _auditLog = auditLog;
    }

    [HttpPost("v1/disputes")]
    [RequireCapability(Capabilities.DisputeFile)]
    public Task<IActionResult> Create(
        [FromBody] CreateDisputeRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => CreateCore(null, request, idempotencyKey, ct);

    [HttpPost("v1/deliveries/{deliveryId}/escalate")]
    [RequireCapability(Capabilities.DisputeFile)]
    public Task<IActionResult> Escalate(
        string deliveryId,
        [FromBody] CreateDisputeRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) => _legacyCases is null
            ? CreateCore(deliveryId, request, idempotencyKey, ct)
            : LegacyEscalate(deliveryId, request, idempotencyKey, ct);

    [HttpGet("v1/disputes")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? cursor,
        [FromQuery] string? deliveryId,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (_legacyCases is not null)
        {
            var legacyItems = await _legacyCases.ListForUserAsync(userId, ct);
            return Ok(new DisputeCaseListResponse
            {
                Items = legacyItems.Select(DisputeCaseResponse.From).ToArray(),
                Total = legacyItems.Count,
            });
        }
        try
        {
            var page = await _cases.ListForUserAsync(GenericCaseKinds.Dispute, userId,
                new GenericCaseQueryV1
                {
                    Status = status, SubjectRef = deliveryId,
                    Limit = Math.Clamp(limit, 1, 200), Cursor = cursor,
                }, ct);
            return Ok(CaseApiProjection.Project(page));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error, GenericCaseKinds.Dispute, "list");
        }
    }

    [HttpGet("v1/deliveries/{deliveryId}/disputes/evidence-preview")]
    [RequireCapability(Capabilities.DisputeFile)]
    public async Task<IActionResult> PreviewEvidence(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            return Ok(await _cases.PreviewDisputeEvidenceAsync(deliveryId, userId,
                CanonicalDeliveryVocab.ActorRoleFor(HttpContext), ct));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error, GenericCaseKinds.Dispute, "evidence_preview");
        }
    }

    [HttpGet("v1/disputes/{id}")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (_legacyCases is not null && id.StartsWith("case_", StringComparison.Ordinal))
            return await LegacyGet(id, userId, ct);
        try
        {
            var detail = await _cases.GetForUserAsync(id, userId, UserIdentity.IsAdmin(HttpContext), ct);
            if (!string.Equals(detail.Case.Kind, GenericCaseKinds.Dispute, StringComparison.Ordinal)) return NotFound();
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, includeInternal: UserIdentity.IsAdmin(HttpContext)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error, GenericCaseKinds.Dispute, "get");
        }
    }

    [HttpPost("v1/disputes/{id}/reply")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    public async Task<IActionResult> Reply(
        string id,
        [FromBody] CaseReplyRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Body)
                && (request.Attachments is null || request.Attachments.Count == 0))
                throw new CaseValidationException("A reply requires a body or attachment.");
            var existing = await _cases.GetForUserAsync(id, userId, isAdmin: false, ct);
            if (!string.Equals(existing.Case.Kind, GenericCaseKinds.Dispute, StringComparison.Ordinal)) return NotFound();
            var detail = await _cases.AddMessageAsync(id, checked((int)RequireVersion(request.ExpectedVersion)),
                request.ReplyToId is null ? "message" : "reply", userId,
                CanonicalDeliveryVocab.ActorRoleFor(HttpContext), RequireIdempotencyKey(idempotencyKey),
                request.Body, request.ReplyToId, request.Attachments, ct);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, includeInternal: false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error, GenericCaseKinds.Dispute, "reply");
        }
    }

    private async Task<IActionResult> CreateCore(
        string? routeDeliveryId,
        CreateDisputeRequestV2? request,
        string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        try
        {
            if (request is null) throw new CaseValidationException("request body is required.");
            var deliveryId = request.ResolveDeliveryId(routeDeliveryId)
                ?? throw new CaseValidationException("deliveryId is required.");
            var key = RequireIdempotencyKey(idempotencyKey);
            var row = await _cases.CreateDisputeAsync(new CreateDisputeCaseInput
            {
                DeliveryId = deliveryId,
                UserId = userId,
                UserRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext),
                Reason = request.Reason ?? "other",
                Comment = request.Comment,
                Attachments = request.ResolveAttachments(),
                VoiceUrl = request.VoiceUrl,
                IncidentCommand = request.IncidentCommand,
                IdempotencyKey = key,
            }, ct);
            Response.Headers.ETag = $"\"{row.Case.Version}\"";
            return CreatedAtAction(nameof(Get), new { id = row.Case.CaseId }, CaseApiProjection.Project(row, false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error, GenericCaseKinds.Dispute, "create");
        }
    }

    private async Task<IActionResult> LegacyEscalate(
        string deliveryId,
        CreateDisputeRequestV2? request,
        string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (request is null)
            return Problem("request body is required.", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var result = await _legacyCases!.EscalateAsync(new EscalateInput
            {
                DeliveryId = deliveryId,
                OpenedByUserId = userId,
                Reason = request.Reason ?? string.Empty,
                Comment = request.Comment,
                PhotoUrls = request.ResolveAttachments(),
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            }, ct);

            if (result.Outcome == EscalateOutcome.DeliveryNotFound)
                return Problem($"Delivery '{deliveryId}' not found.", statusCode: StatusCodes.Status404NotFound);
            if (result.Outcome == EscalateOutcome.AlreadyEscalated)
                return Conflict(new ProblemDetails
                {
                    Title = "An active dispute case already exists for this delivery.",
                    Detail = $"Existing case id: {result.Case!.Id}",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/dispute-already-open",
                });
            if (result.Outcome == EscalateOutcome.Replayed)
                return Ok(DisputeCaseResponse.From(result.Case!));

            if (_auditLog is not null)
            {
                await _auditLog.AppendAsync(new AdminAuditAppend
                {
                    AdminUserId = userId,
                    Action = "escalate_case",
                    EntityType = "dispute_case",
                    EntityId = result.Case!.Id,
                    RequestId = HttpContext.TraceIdentifier,
                }, ct);
            }
            return CreatedAtAction(nameof(Get), new { id = result.Case!.Id }, DisputeCaseResponse.From(result.Case));
        }
        catch (DisputeCaseValidationException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private async Task<IActionResult> LegacyGet(string id, string userId, CancellationToken ct)
    {
        var item = await _legacyCases!.GetAsync(id, ct);
        if (item is null) return NotFound();
        var permitted = UserIdentity.IsAdmin(HttpContext)
            || string.Equals(item.OpenedByUserId, userId, StringComparison.Ordinal)
            || string.Equals(item.CounterpartyUserId, userId, StringComparison.Ordinal);
        return permitted
            ? Ok(DisputeCaseResponse.From(item))
            : StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden: dispute case belongs to a different delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-resource",
            });
    }
}
