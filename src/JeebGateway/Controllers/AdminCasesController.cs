using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Disputes;
using JeebGateway.Disputes.V2;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
public sealed class AdminCasesController : CaseControllerBase
{
    private readonly IGenericCaseGatewayService _cases;
    private readonly IDisputeCaseService? _legacyCases;

    public AdminCasesController(IGenericCaseGatewayService cases, IDisputeCaseService? legacyCases = null)
    {
        _cases = cases;
        _legacyCases = legacyCases;
    }

    [HttpGet("admin/v1/cases")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Queue([FromQuery] string? kind, [FromQuery] string? status,
        [FromQuery] string? priority, [FromQuery] string? assignedTo, [FromQuery] bool? unassigned,
        [FromQuery] DateTimeOffset? dueBefore, [FromQuery] bool? active,
        [FromQuery] int limit = 100, [FromQuery] string? cursor = null, CancellationToken ct = default) =>
        QueueCore(kind, status, priority, assignedTo, unassigned, dueBefore, active, limit, cursor, ct);

    [HttpGet("admin/v1/disputes")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> DisputeQueue([FromQuery] string? status, [FromQuery] string? priority,
        [FromQuery] string? assignedTo, [FromQuery] bool? unassigned, [FromQuery] DateTimeOffset? dueBefore,
        [FromQuery] bool? active, [FromQuery] int limit = 100, [FromQuery] string? cursor = null,
        CancellationToken ct = default) => QueueCore(GenericCaseKinds.Dispute, status, priority,
            assignedTo, unassigned, dueBefore, active, limit, cursor, ct);

    [HttpGet("admin/v1/support/tickets")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> SupportQueue([FromQuery] string? status, [FromQuery] string? priority,
        [FromQuery] string? assignedTo, [FromQuery] bool? unassigned, [FromQuery] DateTimeOffset? dueBefore,
        [FromQuery] bool? active, [FromQuery] int limit = 100, [FromQuery] string? cursor = null,
        CancellationToken ct = default) => QueueCore(GenericCaseKinds.Support, status, priority,
            assignedTo, unassigned, dueBefore, active, limit, cursor, ct);

    [HttpGet("admin/v1/cases/{id}")]
    [HttpGet("admin/v1/disputes/{id}")]
    [HttpGet("admin/v1/support/tickets/{id}")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            var detail = await _cases.GetForUserAsync(id, adminId, true, ct);
            EnsureRouteKind(detail);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, true));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    [HttpPost("admin/v1/cases/{id}/claim")]
    [HttpPost("admin/v1/disputes/{id}/claim")]
    [HttpPost("admin/v1/support/tickets/{id}/claim")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Claim(string id, [FromBody] CaseClaimRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Patch(id, request?.ExpectedVersion, key,
            (version, admin) => new PatchGenericCaseRequestV1 { ExpectedVersion = version, AssigneeRef = admin }, null, ct);

    [HttpPost("admin/v1/cases/{id}/reassign")]
    [HttpPost("admin/v1/disputes/{id}/reassign")]
    [HttpPost("admin/v1/support/tickets/{id}/reassign")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Reassign(string id, [FromBody] CaseReassignRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Patch(id, request?.ExpectedVersion, key, (version, _) => new PatchGenericCaseRequestV1
        {
            ExpectedVersion = version,
            AssigneeRef = string.IsNullOrWhiteSpace(request?.AssigneeUserId) ? null : request.AssigneeUserId.Trim(),
            ClearAssignee = string.IsNullOrWhiteSpace(request?.AssigneeUserId),
        }, null, ct);

    [HttpPost("admin/v1/cases/{id}/priority")]
    [HttpPost("admin/v1/disputes/{id}/priority")]
    [HttpPost("admin/v1/support/tickets/{id}/priority")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Priority(string id, [FromBody] CasePriorityRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        if (!GenericCasePriorities.IsValid(request?.Priority))
            return Task.FromResult<IActionResult>(Problem("priority must be low, normal, high, or urgent.", statusCode: 400));
        return Patch(id, request?.ExpectedVersion, key, (version, _) => new PatchGenericCaseRequestV1
        { ExpectedVersion = version, Priority = request!.Priority }, null, ct);
    }

    [HttpPost("admin/v1/cases/{id}/due")]
    [HttpPost("admin/v1/disputes/{id}/due")]
    [HttpPost("admin/v1/support/tickets/{id}/due")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Due(string id, [FromBody] CaseDueRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Patch(id, request?.ExpectedVersion, key, (version, _) => new PatchGenericCaseRequestV1
        {
            ExpectedVersion = version, DueAt = request?.Clear == true ? null : request?.DueAt,
            ClearDueAt = request?.Clear == true,
        }, null, ct);

    [HttpPost("admin/v1/cases/{id}/reply")]
    [HttpPost("admin/v1/disputes/{id}/reply")]
    [HttpPost("admin/v1/support/tickets/{id}/reply")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Reply(string id, [FromBody] CaseReplyRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Message(id, request?.ExpectedVersion, key, request?.ReplyToId is null ? "message" : "reply",
            request?.Body, request?.ReplyToId, request?.Attachments, ct);

    [HttpPost("admin/v1/cases/{id}/note")]
    [HttpPost("admin/v1/disputes/{id}/note")]
    [HttpPost("admin/v1/support/tickets/{id}/note")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Note(string id, [FromBody] CaseNoteRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Message(id, request?.ExpectedVersion, key, "internal_note", request?.Body, null, null, ct);

    [HttpPost("admin/v1/cases/{id}/mark-fixed")]
    [HttpPost("admin/v1/disputes/{id}/mark-fixed")]
    [HttpPost("admin/v1/support/tickets/{id}/mark-fixed")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> MarkFixed(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Status(id, GenericCaseStatuses.Fixed, request, key, ct);

    [HttpPost("admin/v1/cases/{id}/close")]
    [HttpPost("admin/v1/disputes/{id}/close")]
    [HttpPost("admin/v1/support/tickets/{id}/close")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> Close(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Status(id, GenericCaseStatuses.Closed, request, key, ct);

    [HttpPost("admin/v1/cases/{id}/reopen")]
    [HttpPost("admin/v1/disputes/{id}/reopen")]
    [HttpPost("admin/v1/support/tickets/{id}/reopen")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public async Task<IActionResult> Reopen(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            await EnsureRouteKindAsync(id, adminId, ct);
            var detail = await _cases.ReopenAsync(id, checked((int)RequireVersion(request?.ExpectedVersion)),
                adminId, "admin", RequireIdempotencyKey(key), request?.Reason, ct);
            if (!string.Equals(detail.Case.CaseId.ToString("D"), id, StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers.Location = $"/admin/v1/cases/{detail.Case.CaseId:D}";
                Response.Headers["X-Reopened-From"] = id;
            }
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, true));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    [HttpPost("admin/v1/disputes/{id}/review")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> LegacyReview(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        IsLegacyCase(id)
            ? ReviewLegacyCase(id, ct)
            : LegacyPatch(id, GenericCaseStatuses.Pending, request?.Reason, request?.ExpectedVersion, key, ct);

    [HttpPost("admin/v1/disputes/{id}/resolve")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public Task<IActionResult> LegacyResolve(string id, [FromBody] LegacyCaseResolutionRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var actionValue = request?.Action ?? request?.Outcome ?? request?.Decision;
        if (string.IsNullOrWhiteSpace(actionValue))
            return Task.FromResult<IActionResult>(Problem("An explicit fixed or closed action is required.", statusCode: 400));
        var action = actionValue.Trim().ToLowerInvariant();
        if (request?.RefundUsd is not null || action.Contains("refund", StringComparison.Ordinal))
            return Task.FromResult<IActionResult>(Problem("COD disputes have no refund or wallet action.", statusCode: 400));
        if (IsLegacyCase(id))
            return ResolveLegacyCase(id, action, request!, key, ct);
        var status = action switch
        {
            "fixed" or "fix" or "resolve" or "resolved" or "mark_fixed" or "mark-fixed"
                => GenericCaseStatuses.Fixed,
            "closed" or "close" or "dismiss" or "dismissed"
                => GenericCaseStatuses.Closed,
            _ => null,
        };
        if (status is null)
            return Task.FromResult<IActionResult>(Problem("Action must be an explicit fixed or closed alias.", statusCode: 400));
        return LegacyPatch(id, status, request?.Resolution ?? request?.Reason ?? request?.Notes,
            request?.ExpectedVersion, key, ct);
    }

    private bool IsLegacyCase(string id) =>
        _legacyCases is not null && id.StartsWith("case_", StringComparison.Ordinal);

    private async Task<IActionResult> ReviewLegacyCase(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            var result = await _legacyCases!.MarkUnderReviewAsync(new MarkUnderReviewInput
            {
                CaseId = id,
                AdminUserId = adminId,
            }, ct);
            return result.Outcome switch
            {
                TransitionOutcome.NotFound => NotFound(),
                TransitionOutcome.AlreadyResolved => Conflict(new ProblemDetails
                {
                    Title = "already_resolved",
                    Detail = $"Case {id} is in terminal state '{result.Case!.State}' and cannot be moved to under_review.",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/dispute-already-resolved",
                }),
                _ => Ok(DisputeCaseResponse.From(result.Case!)),
            };
        }
        catch (DisputeCaseValidationException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private async Task<IActionResult> ResolveLegacyCase(
        string id,
        string action,
        LegacyCaseResolutionRequest request,
        string? key,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        if (action is not ("no_action" or "no-action" or "noaction" or "fixed" or "closed"))
            return Problem("Action must be an explicit fixed or closed alias.", statusCode: 400);
        try
        {
            var result = await _legacyCases!.ResolveAsync(new ResolveCaseInput
            {
                CaseId = id,
                AdminUserId = adminId,
                Decision = ResolveDecision.NoAction,
                Notes = request.Resolution ?? request.Reason ?? request.Notes,
                IdempotencyKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            }, ct);
            return result.Outcome switch
            {
                ResolveOutcome.NotFound => NotFound(),
                ResolveOutcome.AlreadyResolved => Conflict(new ProblemDetails
                {
                    Title = "already_resolved",
                    Detail = $"Case {id} is in terminal state '{result.Case!.State}'.",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/dispute-already-resolved",
                }),
                _ => Ok(DisputeCaseResponse.From(result.Case!)),
            };
        }
        catch (DisputeCaseValidationException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DisputeCaseConflictException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private async Task<IActionResult> QueueCore(string? kind, string? status, string? priority,
        string? assignedTo, bool? unassigned, DateTimeOffset? dueBefore, bool? active,
        int limit, string? cursor, CancellationToken ct)
    {
        try
        {
            var page = await _cases.ListAdminAsync(new GenericCaseQueryV1
            {
                Kind = kind, Status = status, Priority = priority, AssigneeRef = assignedTo,
                DueBefore = dueBefore, Active = active, Limit = limit, Cursor = cursor,
            }, unassigned, ct);
            return Ok(CaseApiProjection.Project(page));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    private Task<IActionResult> Status(string id, string status, CaseStatusRequestV1? request,
        string? key, CancellationToken ct) => Patch(id, request?.ExpectedVersion, key,
        (version, _) => new PatchGenericCaseRequestV1 { ExpectedVersion = version, Status = status },
        request?.Reason, ct);

    private async Task<IActionResult> LegacyPatch(string id, string status, string? reason,
        long? suppliedVersion, string? suppliedKey, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            if ((reason?.Length ?? 0) > DisputeService.MaxResolutionLength)
                throw new CaseValidationException(
                    $"resolution must be {DisputeService.MaxResolutionLength} characters or fewer.");
            var detail = await _cases.GetForUserAsync(id, adminId, true, ct);
            if (detail.Case.Kind != GenericCaseKinds.Dispute) throw new CaseNotFoundException("Case was not found.");
            var publicReasonAlreadyPresent = reason is not null
                && detail.Messages.Any(message => message.MessageType != "internal_note"
                    && message.Actor.Role is "admin" or "agent"
                    && string.Equals(message.Body, reason, StringComparison.Ordinal));
            if (detail.Case.Status == status && (reason is null || publicReasonAlreadyPresent))
            {
                Response.Headers.ETag = $"\"{detail.Case.Version}\"";
                return Ok(CaseApiProjection.Project(detail, true));
            }
            var version = suppliedVersion ?? detail.Case.Version;
            var key = string.IsNullOrWhiteSpace(suppliedKey)
                ? GenericCaseGatewayService.DeterministicKey(id, status, version.ToString(), reason)
                : suppliedKey;
            return await Patch(id, version, key,
                (value, _) => new PatchGenericCaseRequestV1 { ExpectedVersion = value, Status = status }, reason, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    private async Task<IActionResult> Patch(string id, long? suppliedVersion, string? suppliedKey,
        Func<int, string, PatchGenericCaseRequestV1> createPatch, string? note, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            await EnsureRouteKindAsync(id, adminId, ct);
            var version = checked((int)RequireVersion(suppliedVersion));
            var key = RequireIdempotencyKey(suppliedKey);
            var patch = createPatch(version, adminId);
            GenericCaseDetailV1 detail;
            if (!string.IsNullOrWhiteSpace(note) && patch.Status is not null)
            {
                detail = await _cases.ApplyStatusMessageAsync(
                    id, version, patch.Status, note, adminId, "admin", key, ct);
            }
            else
            {
                detail = await _cases.PatchAsync(id, patch, adminId,
                    CanonicalDeliveryVocab.ActorRoleFor(HttpContext), key, ct);
            }
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, true));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    private async Task<IActionResult> Message(string id, long? suppliedVersion, string? suppliedKey,
        string messageType, string? body, Guid? replyToId, IReadOnlyList<string>? attachments, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            await EnsureRouteKindAsync(id, adminId, ct);
            if (string.IsNullOrWhiteSpace(body) && (attachments is null || attachments.Count == 0))
                throw new CaseValidationException("A message requires a body or attachment.");
            var detail = await _cases.AddMessageAsync(id, checked((int)RequireVersion(suppliedVersion)),
                messageType, adminId, "admin", RequireIdempotencyKey(suppliedKey), body,
                replyToId, attachments, ct);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(CaseApiProjection.Project(detail, true));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    private async Task EnsureRouteKindAsync(string id, string adminId, CancellationToken ct)
    {
        if (ExpectedRouteKind() is null) return;
        EnsureRouteKind(await _cases.GetForUserAsync(id, adminId, true, ct));
    }

    private void EnsureRouteKind(GenericCaseDetailV1 detail)
    {
        var expected = ExpectedRouteKind();
        if (expected is not null && !string.Equals(detail.Case.Kind, expected, StringComparison.Ordinal))
            throw new CaseNotFoundException("Case was not found.");
    }

    private string? ExpectedRouteKind()
    {
        var path = Request.Path.Value;
        if (path?.StartsWith("/admin/v1/disputes/", StringComparison.OrdinalIgnoreCase) == true)
            return GenericCaseKinds.Dispute;
        if (path?.StartsWith("/admin/v1/support/tickets/", StringComparison.OrdinalIgnoreCase) == true)
            return GenericCaseKinds.Support;
        return null;
    }
}

public sealed class LegacyCaseResolutionRequest
{
    public string? Action { get; init; }
    public string? Outcome { get; init; }
    public string? Decision { get; init; }
    public decimal? RefundUsd { get; init; }
    public string? Resolution { get; init; }
    public string? Reason { get; init; }
    public string? Notes { get; init; }
    public long? ExpectedVersion { get; init; }
}
