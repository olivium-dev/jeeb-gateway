using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Disputes;
using JeebGateway.Disputes.V2;
using JeebGateway.Requests;
using JeebGateway.Services.Cdn;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
public sealed class AdminCasesController : CaseControllerBase
{
    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web);
    private readonly IGenericCaseGatewayService _cases;
    private readonly IDisputeCaseService? _legacyCases;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;

    public AdminCasesController(
        IGenericCaseGatewayService cases,
        IHttpClientFactory clients,
        IConfiguration configuration,
        IDisputeCaseService? legacyCases = null)
    {
        _cases = cases;
        _clients = clients;
        _configuration = configuration;
        _legacyCases = legacyCases;
    }

    [HttpGet("admin/v1/cases")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    [ProducesResponseType(typeof(CasePageResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> Queue([FromQuery] string? query, [FromQuery] string? kind, [FromQuery] string? status,
        [FromQuery] string? priority, [FromQuery] string? assignedTo, [FromQuery] bool? unassigned,
        [FromQuery] DateTimeOffset? dueBefore, [FromQuery] bool? active,
        [FromQuery] string? sort = null, [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null, CancellationToken ct = default) =>
        QueueCore(query, kind, status, priority, assignedTo, unassigned, dueBefore, active, sort, limit, cursor, ct);

    [HttpGet("admin/v1/disputes")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    [ProducesResponseType(typeof(CasePageResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> DisputeQueue([FromQuery] string? query, [FromQuery] string? status, [FromQuery] string? priority,
        [FromQuery] string? assignedTo, [FromQuery] bool? unassigned, [FromQuery] DateTimeOffset? dueBefore,
        [FromQuery] bool? active, [FromQuery] string? sort = null, [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null, CancellationToken ct = default) =>
        QueueCore(query, GenericCaseKinds.Dispute, status, priority,
            assignedTo, unassigned, dueBefore, active, sort, limit, cursor, ct);

    [HttpGet("admin/v1/support/tickets")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    [ProducesResponseType(typeof(CasePageResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> SupportQueue([FromQuery] string? query, [FromQuery] string? status, [FromQuery] string? priority,
        [FromQuery] string? assignedTo, [FromQuery] bool? unassigned, [FromQuery] DateTimeOffset? dueBefore,
        [FromQuery] bool? active, [FromQuery] string? sort = null, [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null, CancellationToken ct = default) =>
        QueueCore(query, GenericCaseKinds.Support, status, priority,
            assignedTo, unassigned, dueBefore, active, sort, limit, cursor, ct);

    [HttpGet("admin/v1/cases/{id}")]
    [HttpGet("admin/v1/disputes/{id}")]
    [HttpGet("admin/v1/support/tickets/{id}")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            var detail = await _cases.GetForUserAsync(id, adminId, true, ct);
            EnsureRouteKind(detail);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(AdminProjection(detail));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    [HttpGet("admin/v1/cases/{id}/messages")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    [ProducesResponseType(typeof(CaseMessagePageResponseV2), StatusCodes.Status200OK)]
    public async Task<IActionResult> Messages(string id, [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            var page = await _cases.ListMessagesForUserAsync(id, adminId, isAdmin: true, limit, cursor, ct);
            var visibleItems = page.Items
                .Where(message => !CaseApiProjection.IsSyntheticMetadataMessage(message))
                .ToArray();
            var response = new CaseMessagePageResponseV2
            {
                Items = visibleItems,
                Total = visibleItems.Length,
                NextCursor = page.NextCursor,
            };
            var node = JsonSerializer.SerializeToNode(response, AdminJson);
            if (node is not null) RewriteCaseEvidence(node, id);
            return Ok(node);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    [HttpGet("admin/v1/cases/{id}/evidence/{token}")]
    [RequireCapability(Capabilities.AdminCasesRead)]
    public async Task<IActionResult> Evidence(string id, string token, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
            return unauthorized;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128
            || string.IsNullOrWhiteSpace(EvidenceTokenKey()))
            return BadRequest();

        GenericCaseDetailV1 detail;
        try
        {
            detail = await _cases.GetForUserAsync(id, adminId, true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CaseProblem(error);
        }

        var projection = JsonSerializer.SerializeToNode(CaseApiProjection.Project(detail, true), AdminJson);
        var reference = projection is null
            ? null
            : EnumerateCaseEvidenceReferences(projection)
                .FirstOrDefault(candidate => TokenMatches(token, CreateEvidenceToken(id, candidate)));
        if (reference is null) return NotFound();

        var cdn = _clients.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (cdn.BaseAddress is null || !TryGetOwnedObjectReference(reference, cdn.BaseAddress, out var objectReference))
            return NotFound();
        var upstreamUri = new Uri(
            cdn.BaseAddress,
            CdnUploadUrlResolver.CdnFetchPathPrefix + Uri.EscapeDataString(objectReference));
        if (!CdnUploadUrlResolver.IsOnFetchPrefix(upstreamUri, cdn.BaseAddress)) return BadRequest();

        HttpResponseMessage upstream;
        try
        {
            upstream = await cdn.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Problem("The case evidence source is unavailable.", statusCode: 503);
        }

        HttpContext.Response.RegisterForDispose(upstream);
        Response.Headers.CacheControl = "private, no-store";
        if (upstream.StatusCode == HttpStatusCode.NotFound) return NotFound();
        if (!upstream.IsSuccessStatusCode)
            return Problem("The case evidence source is unavailable.", statusCode: 503);
        if (!AdminEvidenceResponsePolicy.HasSafeLength(upstream.Content.Headers.ContentLength))
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!AdminEvidenceResponsePolicy.TryApply(
                Response, upstream.Content.Headers.ContentType?.ToString(), out var contentType))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var declaredLength = upstream.Content.Headers.ContentLength!.Value;
        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return File(AdminEvidenceResponsePolicy.EnforceDeclaredLength(stream, declaredLength), contentType);
    }

    [HttpPost("admin/v1/cases/{id}/claim")]
    [HttpPost("admin/v1/disputes/{id}/claim")]
    [HttpPost("admin/v1/support/tickets/{id}/claim")]
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> Claim(string id, [FromBody] CaseClaimRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Patch(id, request?.ExpectedVersion, key,
            (version, admin) => new PatchGenericCaseRequestV1 { ExpectedVersion = version, AssigneeRef = admin }, null, ct);

    [HttpPost("admin/v1/cases/{id}/reassign")]
    [HttpPost("admin/v1/disputes/{id}/reassign")]
    [HttpPost("admin/v1/support/tickets/{id}/reassign")]
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
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
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
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
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
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
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> Reply(string id, [FromBody] CaseReplyRequestV2? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Message(id, request?.ExpectedVersion, key, request?.ReplyToId is null ? "message" : "reply",
            request?.Body, request?.ReplyToId, request?.Attachments, ct);

    [HttpPost("admin/v1/cases/{id}/note")]
    [HttpPost("admin/v1/disputes/{id}/note")]
    [HttpPost("admin/v1/support/tickets/{id}/note")]
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> Note(string id, [FromBody] CaseNoteRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Message(id, request?.ExpectedVersion, key, "internal_note", request?.Body, null, null, ct);

    [HttpPost("admin/v1/cases/{id}/mark-fixed")]
    [HttpPost("admin/v1/disputes/{id}/mark-fixed")]
    [HttpPost("admin/v1/support/tickets/{id}/mark-fixed")]
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> MarkFixed(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Status(id, GenericCaseStatuses.Fixed, request, key, ct);

    [HttpPost("admin/v1/cases/{id}/close")]
    [HttpPost("admin/v1/disputes/{id}/close")]
    [HttpPost("admin/v1/support/tickets/{id}/close")]
    [RequireCapability(Capabilities.AdminCasesClose)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public Task<IActionResult> Close(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        Status(id, GenericCaseStatuses.Closed, request, key, ct);

    [HttpPost("admin/v1/cases/{id}/reopen")]
    [HttpPost("admin/v1/disputes/{id}/reopen")]
    [HttpPost("admin/v1/support/tickets/{id}/reopen")]
    [RequireCapability(Capabilities.AdminCasesClose)]
    [ProducesResponseType(typeof(CaseDetailResponseV2), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        try
        {
            await EnsureRouteKindAsync(id, adminId, ct);
            var detail = await _cases.ReopenAsync(id, checked((int)RequireVersion(request?.ExpectedVersion)),
                adminId, CaseActorRole(), RequireIdempotencyKey(key), request?.Reason, ct);
            if (!string.Equals(detail.Case.CaseId.ToString("D"), id, StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers.Location = $"/admin/v1/cases/{detail.Case.CaseId:D}";
                Response.Headers["X-Reopened-From"] = id;
            }
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(AdminProjection(detail));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    [HttpPost("admin/v1/disputes/{id}/review")]
    [RequireCapability(Capabilities.AdminCasesUpdate)]
    public Task<IActionResult> LegacyReview(string id, [FromBody] CaseStatusRequestV1? request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        IsLegacyCase(id)
            ? ReviewLegacyCase(id, ct)
            : LegacyPatch(id, GenericCaseStatuses.Pending, request?.Reason, request?.ExpectedVersion, key, ct);

    [HttpPost("admin/v1/disputes/{id}/resolve")]
    [RequireCapability(Capabilities.AdminCasesClose)]
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

    private async Task<IActionResult> QueueCore(string? query, string? kind, string? status, string? priority,
        string? assignedTo, bool? unassigned, DateTimeOffset? dueBefore, bool? active,
        string? sort, int limit, string? cursor, CancellationToken ct)
    {
        try
        {
            var page = await _cases.ListAdminAsync(new GenericCaseQueryV1
            {
                Query = query, Kind = kind, Status = status, Priority = priority, AssigneeRef = assignedTo,
                DueBefore = dueBefore, Active = active, Sort = sort, Limit = limit, Cursor = cursor,
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
                return Ok(AdminProjection(detail));
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
                    id, version, patch.Status, note, adminId, CaseActorRole(), key, ct);
            }
            else
            {
                detail = await _cases.PatchAsync(id, patch, adminId,
                    CaseActorRole(), key, ct);
            }
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(AdminProjection(detail));
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
                messageType, adminId, CaseActorRole(), RequireIdempotencyKey(suppliedKey), body,
                replyToId, attachments, ct);
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(AdminProjection(detail));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return CaseProblem(error); }
    }

    private async Task EnsureRouteKindAsync(string id, string adminId, CancellationToken ct)
    {
        if (ExpectedRouteKind() is null) return;
        EnsureRouteKind(await _cases.GetForUserAsync(id, adminId, true, ct));
    }

    private string CaseActorRole()
    {
        if (UserIdentity.HasRole(HttpContext, Roles.Admin)) return "admin";
        if (UserIdentity.HasRole(HttpContext, Roles.Support)
            || UserIdentity.HasRole(HttpContext, Roles.SupportLead)) return "agent";
        return "system";
    }

    private void EnsureRouteKind(GenericCaseDetailV1 detail)
    {
        var expected = ExpectedRouteKind();
        if (expected is not null && !string.Equals(detail.Case.Kind, expected, StringComparison.Ordinal))
            throw new CaseNotFoundException("Case was not found.");
    }

    private JsonNode? AdminProjection(GenericCaseDetailV1 detail)
    {
        var node = JsonSerializer.SerializeToNode(CaseApiProjection.Project(detail, true), AdminJson);
        if (node is not null) RewriteCaseEvidence(node, detail.Case.CaseId.ToString("D"));
        return node;
    }

    private void RewriteCaseEvidence(JsonNode node, string caseId)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && IsSingleEvidenceProperty(property.Key))
                {
                    obj[property.Key] = EvidencePath(caseId, reference);
                    continue;
                }

                if (property.Value is JsonArray array && IsEvidenceArrayProperty(property.Key))
                {
                    for (var index = 0; index < array.Count; index++)
                    {
                        if (array[index] is JsonValue item
                            && item.TryGetValue<string>(out var arrayReference))
                            array[index] = EvidencePath(caseId, arrayReference);
                        else if (array[index] is not null)
                            RewriteCaseEvidence(array[index]!, caseId);
                    }
                    continue;
                }

                if (property.Value is not null) RewriteCaseEvidence(property.Value, caseId);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                if (child is not null) RewriteCaseEvidence(child, caseId);
        }
    }

    private static IEnumerable<string> EnumerateCaseEvidenceReferences(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && IsSingleEvidenceProperty(property.Key)
                    && !string.IsNullOrWhiteSpace(reference))
                    yield return reference;

                if (property.Value is JsonArray array && IsEvidenceArrayProperty(property.Key))
                    foreach (var item in array)
                        if (item is JsonValue arrayValue
                            && arrayValue.TryGetValue<string>(out var arrayReference)
                            && !string.IsNullOrWhiteSpace(arrayReference))
                            yield return arrayReference;

                if (property.Value is not null)
                    foreach (var child in EnumerateCaseEvidenceReferences(property.Value))
                        yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var childNode in array)
                if (childNode is not null)
                    foreach (var child in EnumerateCaseEvidenceReferences(childNode))
                        yield return child;
        }
    }

    private string? EvidencePath(string caseId, string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(EvidenceTokenKey())
            ? null
            : $"/gateway/admin/v1/cases/{Uri.EscapeDataString(caseId)}/evidence/{CreateEvidenceToken(caseId, reference)}";

    private string CreateEvidenceToken(string caseId, string reference)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(EvidenceTokenKey()));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{caseId}\n{reference}"));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private string EvidenceTokenKey() =>
        _configuration["AdminEvidence:TokenKey"] ?? string.Empty;

    private static bool TokenMatches(string supplied, string expected)
    {
        var suppliedBytes = Encoding.ASCII.GetBytes(supplied);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static bool IsSingleEvidenceProperty(string name) =>
        string.Equals(name, "cdnRef", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "voiceUrl", StringComparison.OrdinalIgnoreCase);

    private static bool IsEvidenceArrayProperty(string name) =>
        string.Equals(name, "photos", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "attachments", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "objectRefs", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetOwnedObjectReference(
        string reference, Uri cdnBaseAddress, out string objectReference)
    {
        objectReference = string.Empty;
        var candidate = reference.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if ((absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                || !string.Equals(absolute.Host, cdnBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
                || absolute.Port != cdnBaseAddress.Port)
                return false;
            var marker = "/" + CdnUploadUrlResolver.CdnFetchPathPrefix;
            if (!absolute.AbsolutePath.StartsWith(marker, StringComparison.Ordinal)) return false;
            candidate = Uri.UnescapeDataString(absolute.AbsolutePath[marker.Length..]);
        }

        candidate = candidate.TrimStart('/');
        if (candidate.Length is not (> 0 and <= 512)
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('%')
            || candidate.Contains('\\')
            || candidate.Contains('?')
            || candidate.Contains('#'))
            return false;
        objectReference = candidate;
        return true;
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
