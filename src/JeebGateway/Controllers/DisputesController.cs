using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Disputes;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace JeebGateway.Controllers;

/// <summary>Compatibility adapter over the generic state-service case engine.</summary>
[ApiController]
public sealed class DisputesController : CaseControllerBase
{
    private const int LegacyPageSize = 200;
    private const int HydrationBatchSize = 8;
    private readonly IGenericCaseGatewayService _cases;
    private readonly IDisputeService? _legacy;
    private readonly IRequestsStore _requests;

    public DisputesController(
        IGenericCaseGatewayService cases,
        IRequestsStore requests,
        IDisputeService? legacy = null)
    {
        _cases = cases;
        _requests = requests;
        _legacy = legacy;
    }

    [HttpPost("deliveries/{deliveryId}/dispute")]
    [RequireCapability(Capabilities.DisputeFile)]
    public async Task<IActionResult> File(string deliveryId, [FromBody] FileDisputeRequest? body,
        [FromHeader(Name = "Idempotency-Key")] string? suppliedKey, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (_legacy is not null)
            return await LegacyFileAsync(deliveryId, body, userId, ct);
        try
        {
            if (body is null) throw new CaseValidationException("request body is required.");
            var normalized = NormalizeLegacyCreate(body);
            var key = string.IsNullOrWhiteSpace(suppliedKey)
                ? GenericCaseGatewayService.DeterministicKey(
                    "legacy-dispute-create", userId, deliveryId, CanonicalCreateRequest(normalized))
                : suppliedKey.Trim();
            var row = await _cases.CreateDisputeAsync(new CreateDisputeCaseInput
            {
                DeliveryId = deliveryId,
                UserId = userId,
                UserRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext),
                Reason = normalized.Category,
                Comment = normalized.Description,
                Attachments = normalized.PhotoUrls,
                IdempotencyKey = key,
            }, ct);
            Response.Headers.ETag = $"\"{row.Case.Version}\"";
            return CreatedAtAction(nameof(GetOne), new { id = row.Case.CaseId }, Legacy(row));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Dispute, "legacy_create"); }
    }

    [HttpGet("disputes")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (_legacy is not null)
        {
            var items = await _legacy.ListForUserAsync(userId, ct);
            return Ok(new DisputeListResponse
            {
                Items = items.Select(DisputeResponse.From).ToArray(), Total = items.Count,
            });
        }
        try
        {
            var rows = new List<GenericCaseV1>();
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await _cases.ListForRequesterAsync(GenericCaseKinds.Dispute, userId,
                    new GenericCaseQueryV1
                    {
                        RequesterRef = userId,
                        Limit = LegacyPageSize,
                        Cursor = cursor,
                    }, ct);
                rows.AddRange(page.Items.Where(row =>
                    row.Kind == GenericCaseKinds.Dispute
                    && string.Equals(row.RequesterRef, userId, StringComparison.Ordinal)));
                cursor = page.NextCursor;
                if (cursor is not null && !seenCursors.Add(cursor))
                    throw new InvalidOperationException("State-service case pagination repeated a cursor.");
            } while (cursor is not null);

            var distinctRows = rows.DistinctBy(row => row.CaseId).ToArray();
            var details = new GenericCaseDetailV1[distinctRows.Length];
            for (var offset = 0; offset < distinctRows.Length; offset += HydrationBatchSize)
            {
                var end = Math.Min(offset + HydrationBatchSize, distinctRows.Length);
                var tasks = Enumerable.Range(offset, end - offset).Select(async index =>
                {
                    details[index] = await _cases.GetForRequesterAsync(
                        distinctRows[index].CaseId.ToString("D"), userId, ct);
                });
                await Task.WhenAll(tasks);
            }
            var items = details.Where(detail =>
                    detail.Case.Kind == GenericCaseKinds.Dispute
                    && string.Equals(detail.Case.RequesterRef, userId, StringComparison.Ordinal))
                .Select(Legacy).ToArray();
            return Ok(new DisputeListResponse { Items = items, Total = items.Length });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Dispute, "legacy_list"); }
    }

    [HttpGet("disputes/{id}")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    public async Task<IActionResult> GetOne(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (_legacy is not null)
        {
            var dispute = await _legacy.GetAsync(id, ct);
            if (dispute is not null)
            {
                if (!UserIdentity.IsAdmin(HttpContext)
                    && !string.Equals(dispute.FiledByUserId, userId, StringComparison.Ordinal))
                    return Problem("Dispute belongs to another user.", statusCode: StatusCodes.Status403Forbidden);
                return Ok(DisputeResponse.From(dispute));
            }
        }
        try
        {
            var isAdmin = UserIdentity.IsAdmin(HttpContext);
            var detail = isAdmin
                ? await _cases.GetForUserAsync(id, userId, isAdmin: true, ct)
                : await _cases.GetForRequesterAsync(id, userId, ct);
            if (detail.Case.Kind != GenericCaseKinds.Dispute) return NotFound();
            if (!isAdmin && !string.Equals(detail.Case.RequesterRef, userId, StringComparison.Ordinal))
                throw new CaseAccessDeniedException();
            Response.Headers.ETag = $"\"{detail.Case.Version}\"";
            return Ok(Legacy(detail));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Dispute, "legacy_get"); }
    }

    [HttpPut("admin/disputes/{id}/resolve")]
    [RequireCapability(Capabilities.DisputeResolve)]
    public async Task<IActionResult> Resolve(string id, [FromBody] ResolveDisputeRequest? body,
        [FromHeader(Name = "Idempotency-Key")] string? suppliedKey, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        if (_legacy is not null)
            return await LegacyResolveAsync(id, body, adminId, ct);
        try
        {
            if (body is null) throw new CaseValidationException("request body is required.");
            var action = body.Action?.Trim().ToLowerInvariant();
            var status = action switch
            {
                "open" or "under_review" => GenericCaseStatuses.Pending,
                "resolve" or "resolved" => GenericCaseStatuses.Fixed,
                "dismiss" or "dismissed" => GenericCaseStatuses.Closed,
                _ => throw new CaseValidationException("action is required (open, resolve, or dismiss)."),
            };
            var resolution = body.Resolution?.Trim();
            if (status is GenericCaseStatuses.Fixed or GenericCaseStatuses.Closed)
            {
                if (string.IsNullOrWhiteSpace(resolution))
                    throw new CaseValidationException("resolution is required for resolve or dismiss.");
                if (resolution.Length > DisputeService.MaxResolutionLength)
                    throw new CaseValidationException(
                        $"resolution must be {DisputeService.MaxResolutionLength} characters or fewer.");
            }
            var publicReason = string.IsNullOrWhiteSpace(resolution)
                ? "Your dispute is under review."
                : resolution;
            var detail = await _cases.GetForUserAsync(id, adminId, true, ct);
            if (detail.Case.Kind != GenericCaseKinds.Dispute) return NotFound();
            var existing = Legacy(detail);
            if (DisputeState.IsTerminal(existing.State))
            {
                var targetState = status == GenericCaseStatuses.Fixed
                    ? DisputeState.Resolved
                    : status == GenericCaseStatuses.Closed ? DisputeState.Dismissed : DisputeState.UnderReview;
                if (existing.State == targetState
                    && string.Equals(existing.Resolution, publicReason, StringComparison.Ordinal))
                {
                    Response.Headers.ETag = $"\"{detail.Case.Version}\"";
                    return Ok(existing);
                }
                throw new CaseConflictException("The legacy dispute is already terminal.");
            }
            var key = string.IsNullOrWhiteSpace(suppliedKey)
                ? GenericCaseGatewayService.DeterministicKey(
                    "legacy-dispute-resolve", adminId, id, CanonicalResolveRequest(action!, resolution))
                : suppliedKey.Trim();
            var updated = await _cases.ApplyStatusMessageAsync(
                id, detail.Case.Version, status, publicReason, adminId,
                CanonicalDeliveryVocab.ActorRoleFor(HttpContext), key, ct);
            Response.Headers.ETag = $"\"{updated.Case.Version}\"";
            return Ok(Legacy(updated));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CaseProblem(error, GenericCaseKinds.Dispute, "legacy_resolve"); }
    }

    private static DisputeResponse Legacy(GenericCaseDetailV1 detail)
    {
        var row = detail.Case;
        var projected = CaseApiProjection.Project(detail, includeInternal: false);
        var review = detail.Audit.LastOrDefault(item =>
            item.EventType == "case.updated"
            && item.Data.ValueKind == JsonValueKind.Object
            && item.Data.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(status.GetString()));
        var publicResolution = detail.Messages.LastOrDefault(message =>
            message.MessageType != "internal_note"
            && message.Actor.Role is "admin" or "agent")?.Body;
        return new DisputeResponse
        {
            Id = "dsp_" + row.CaseId.ToString("D"),
            DeliveryId = row.Subject.Ref,
            FiledByUserId = row.RequesterRef,
            Category = LegacyCategory(projected.Reason),
            Description = projected.Comment ?? string.Empty,
            PhotoUrls = projected.Photos,
            State = LegacyState(row.Status, review is not null),
            FiledAt = row.CreatedAt,
            ReviewedAt = review?.CreatedAt,
            ResolverAdminId = review?.Actor.Ref ?? row.AssigneeRef,
            Resolution = row.Status is GenericCaseStatuses.Fixed or GenericCaseStatuses.Closed
                ? publicResolution : null,
        };
    }

    private static LegacyCreate NormalizeLegacyCreate(FileDisputeRequest body)
    {
        var category = body.Category?.Trim() ?? string.Empty;
        if (!DisputeCategory.IsValid(category))
            throw new CaseValidationException("category is required and must be supported.");
        var description = body.Description?.Trim() ?? string.Empty;
        if (description.Length == 0)
            throw new CaseValidationException("description is required.");
        if (description.Length > DisputeService.MaxDescriptionLength)
            throw new CaseValidationException(
                $"description must be {DisputeService.MaxDescriptionLength} characters or fewer.");
        if ((body.PhotoUrls?.Count ?? 0) > 3)
            throw new CaseValidationException("A maximum of 3 photo URLs is allowed.");
        var photos = new List<string>();
        foreach (var value in body.PhotoUrls ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var url = value.Trim();
            if (!(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                  || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || url.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)))
                throw new CaseValidationException("photo URLs must start with https://, http://, or s3://.");
            photos.Add(url);
        }
        return new LegacyCreate(category, description, photos);
    }

    private async Task<IActionResult> LegacyFileAsync(
        string deliveryId, FileDisputeRequest? body, string userId, CancellationToken ct)
    {
        if (body is null) return Problem("request body is required.", statusCode: 400);
        if (await _requests.GetAsync(deliveryId, ct) is null) return NotFound();
        try
        {
            var dispute = await _legacy!.FileAsync(new FileDisputeInput
            {
                DeliveryId = deliveryId,
                FiledByUserId = userId,
                Category = body.Category ?? string.Empty,
                Description = body.Description ?? string.Empty,
                PhotoUrls = body.PhotoUrls ?? new List<string>(),
            }, ct);
            return CreatedAtAction(nameof(GetOne), new { id = dispute.Id }, DisputeResponse.From(dispute));
        }
        catch (DisputeValidationException error)
        {
            return Problem(error.Message, statusCode: 400);
        }
        catch (DisputeConflictException error)
        {
            return Problem(error.Message, statusCode: 409);
        }
    }

    private async Task<IActionResult> LegacyResolveAsync(
        string id, ResolveDisputeRequest? body, string adminId, CancellationToken ct)
    {
        if (body is null) return Problem("request body is required.", statusCode: 400);
        var action = body.Action?.Trim().ToLowerInvariant() switch
        {
            "open" or "under_review" => DisputeResolveAction.Open,
            "resolve" or "resolved" => DisputeResolveAction.Resolve,
            "dismiss" or "dismissed" => DisputeResolveAction.Dismiss,
            _ => (DisputeResolveAction?)null,
        };
        if (action is null)
            return Problem("action is required (open, resolve, or dismiss).", statusCode: 400);
        try
        {
            var dispute = await _legacy!.ResolveAsync(id, new ResolveDisputeInput
            {
                Action = action.Value, AdminUserId = adminId, Resolution = body.Resolution,
            }, ct);
            return dispute is null ? NotFound() : Ok(DisputeResponse.From(dispute));
        }
        catch (DisputeValidationException error)
        {
            return Problem(error.Message, statusCode: 400);
        }
        catch (DisputeConflictException error)
        {
            return Problem(error.Message, statusCode: 409);
        }
    }

    private static string CanonicalCreateRequest(LegacyCreate body) => JsonSerializer.Serialize(new
    {
        category = body.Category,
        description = body.Description,
        photoUrls = body.PhotoUrls,
    });

    private static string CanonicalResolveRequest(string action, string? resolution) => JsonSerializer.Serialize(new
    {
        action,
        resolution,
    });

    private sealed record LegacyCreate(
        string Category,
        string Description,
        IReadOnlyList<string> PhotoUrls);

    private static string LegacyState(string status, bool reviewed) => status switch
    {
        GenericCaseStatuses.Pending => reviewed ? DisputeState.UnderReview : DisputeState.Filed,
        GenericCaseStatuses.Fixed => DisputeState.Resolved,
        GenericCaseStatuses.Closed => DisputeState.Dismissed,
        _ => DisputeState.Filed,
    };

    private static string LegacyCategory(string? category) => category switch
    {
        "damaged" => DisputeCategory.DamagedGoods,
        "wrong_item" => DisputeCategory.WrongDelivery,
        "no_show" => DisputeCategory.NoDelivery,
        null or "" => "other",
        _ => category,
    };
}
