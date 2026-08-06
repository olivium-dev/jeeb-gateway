using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;

namespace JeebGateway.Cases;

public interface IGenericCaseGatewayService
{
    Task<GenericCaseDetailV1> CreateDisputeAsync(CreateDisputeCaseInput input, CancellationToken ct);
    Task<GenericCaseDetailV1> CreateSupportAsync(CreateSupportCaseInput input, CancellationToken ct);
    Task<GenericCaseDetailV1> GetForUserAsync(string caseId, string userId, bool isAdmin, CancellationToken ct);
    Task<GenericCaseDetailV1> GetForRequesterAsync(
        string caseId, string requesterRef, CancellationToken ct) =>
        Task.FromException<GenericCaseDetailV1>(
            new NotSupportedException("Persisted requester reads are not implemented by this adapter."));
    Task<GenericCasePageV1> ListForUserAsync(string kind, string userId, GenericCaseQueryV1 query, CancellationToken ct);
    Task<GenericCasePageV1> ListForRequesterAsync(
        string kind, string requesterRef, GenericCaseQueryV1 query, CancellationToken ct) =>
        Task.FromException<GenericCasePageV1>(
            new NotSupportedException("Persisted requester lists are not implemented by this adapter."));
    async Task<GenericCaseMessagePageV1> ListMessagesForUserAsync(
        string caseId, string userId, bool isAdmin, int limit, string? cursor, CancellationToken ct)
    {
        var detail = await GetForUserAsync(caseId, userId, isAdmin, ct);
        if (!string.Equals(detail.Case.Kind, GenericCaseKinds.Support, StringComparison.Ordinal))
            throw new CaseNotFoundException("Case was not found.");
        var window = CaseCursorPagination.Messages(detail.Messages, cursor, limit);
        return new GenericCaseMessagePageV1 { Items = window.Items, NextCursor = window.NextCursor };
    }
    Task<DisputeEvidencePreviewResponseV1> PreviewDisputeEvidenceAsync(
        string deliveryId, string userId, string userRole, CancellationToken ct) =>
        Task.FromException<DisputeEvidencePreviewResponseV1>(
            new NotSupportedException("Dispute evidence preview is not implemented by this case adapter."));
    Task<GenericCasePageV1> ListAdminAsync(GenericCaseQueryV1 query, bool? unassigned, CancellationToken ct);
    Task<GenericCaseDetailV1> PatchAsync(string caseId, PatchGenericCaseRequestV1 patch,
        string actorId, string actorRole, string idempotencyKey, CancellationToken ct);
    Task<GenericCaseDetailV1> ApplyStatusMessageAsync(string caseId, int expectedVersion,
        string status, string body, string actorId, string actorRole,
        string idempotencyKey, CancellationToken ct) =>
        Task.FromException<GenericCaseDetailV1>(
            new NotSupportedException("Atomic case status messages are not implemented by this adapter."));
    Task<GenericCaseDetailV1> AddMessageAsync(string caseId, int expectedVersion,
        string messageType, string actorId, string actorRole, string idempotencyKey,
        string? body, Guid? replyToId, IReadOnlyList<string>? attachments, CancellationToken ct);
    Task<GenericCaseDetailV1> ReopenAsync(string caseId, int expectedVersion,
        string actorId, string actorRole, string idempotencyKey, string? reason, CancellationToken ct);
}

public sealed class GenericCaseGatewayService : IGenericCaseGatewayService
{
    public const string ActivateIncidentCommand = "activate_delivery_incident";
    public const int MaxAttachments = 5;
    private const int MetadataMessageBudget = 48_000;
    private const int MetadataProbeSize = 20;
    private const int DetailMessageLimit = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> DisputeReasons = new(StringComparer.Ordinal)
    {
        "damaged", "damaged_goods", "wrong_item", "wrong_delivery", "no_show", "no_delivery",
        "fraud", "abuse", "safety_concern", "prohibited_item", "other", "overcharged",
    };

    private readonly IGenericCaseStateClient _cases;
    private readonly ICaseDeliveryClient _delivery;
    private readonly ICaseEvidenceCollector _evidence;
    private readonly TimeProvider _clock;
    private readonly ILogger<GenericCaseGatewayService> _log;

    public GenericCaseGatewayService(IGenericCaseStateClient cases, ICaseDeliveryClient delivery,
        ICaseEvidenceCollector evidence, TimeProvider clock, ILogger<GenericCaseGatewayService> log)
    {
        _cases = cases;
        _delivery = delivery;
        _evidence = evidence;
        _clock = clock;
        _log = log;
    }

    public async Task<GenericCaseDetailV1> CreateDisputeAsync(CreateDisputeCaseInput input, CancellationToken ct)
    {
        ValidateDispute(input);
        var delivery = await GetDeliveryCaseContextAsync(input.DeliveryId, ct);
        var actorRole = ResolvePartyRole(delivery, input.UserId, input.UserRole);
        using var activity = CaseTelemetry.Activities.StartActivity("case.create", ActivityKind.Internal);
        activity?.SetTag("case.kind", GenericCaseKinds.Dispute);
        activity?.SetTag("delivery.id", input.DeliveryId);

        var attachments = AttachmentCreates(input.Attachments, input.VoiceUrl);
        var created = await MeasureAsync("create", () => _cases.CreateCaseAsync(
            new CreateGenericCaseRequestV1
            {
                Kind = GenericCaseKinds.Dispute,
                Category = NormalizeDisputeReason(input.Reason),
                Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = input.DeliveryId },
                RequesterRef = input.UserId,
                ParticipantRefs = CanonicalParticipantRefs(delivery),
                Status = GenericCaseStatuses.Pending,
                Priority = GenericCasePriorities.Normal,
                Attachments = attachments,
            }, input.IdempotencyKey, input.UserId, actorRole, ct), ct, GenericCaseKinds.Dispute);
        var row = await _cases.GetCaseAsync(created.CaseId, ct);
        var persistedMessages = await _cases.GetCaseMessagesAsync(created.CaseId, includeInternal: true, ct);

        if (!persistedMessages.Any(message => message.MessageType != "internal_note"))
        {
            row = await AddInitialMessageAsync(row, input.IdempotencyKey, input.UserId,
                actorRole, NullIfBlank(input.Comment) ?? input.Reason.Trim(), ct);
        }
        if (!persistedMessages.Any(IsGatewayMetadata))
        {
            var evidence = await _evidence.CaptureAsync(
                input.DeliveryId, input.UserId, input.Attachments, ct);
            row = await AddMetadataAsync(row, input.IdempotencyKey, new CaseGatewayMetadataV1
            {
                Subject = $"Delivery dispute: {input.Reason.Trim()}",
                VoiceUrl = NullIfBlank(input.VoiceUrl),
                IncidentCommand = NullIfBlank(input.IncidentCommand),
                Evidence = CompactEvidence(evidence),
            }, ct);
        }

        if (!string.IsNullOrWhiteSpace(input.IncidentCommand)
            && !persistedMessages.Any(IsDeliveryIncidentOutcomeAudit))
            row = await ActivateIncidentSafelyAsync(row, input, actorRole, delivery, ct);

        row = await _cases.GetCaseAsync(created.CaseId, ct);

        activity?.SetTag("case.id", row.CaseId);
        _log.LogInformation(
            "event=case.created case_id={CaseId} case_kind=dispute delivery_id={DeliveryId} "
            + "actor_id={ActorId} version={Version} correlation_id={CorrelationId}",
            row.CaseId, input.DeliveryId, input.UserId, row.Version,
            Activity.Current?.TraceId.ToString() ?? "none");
        CaseTelemetry.Requests.Add(1, new("kind", "dispute"), new("operation", "create"), new("outcome", "success"));
        return await LoadDetailAsync(row, ct);
    }

    public async Task<GenericCaseDetailV1> CreateSupportAsync(CreateSupportCaseInput input, CancellationToken ct)
    {
        ValidateSupport(input);
        using var activity = CaseTelemetry.Activities.StartActivity("case.create", ActivityKind.Internal);
        activity?.SetTag("case.kind", GenericCaseKinds.Support);
        var actorRole = EnsureEndUserRole(input.UserRole);
        if (!string.IsNullOrWhiteSpace(input.OrderId))
        {
            var delivery = await GetDeliveryCaseContextAsync(input.OrderId, ct);
            actorRole = ResolvePartyRole(delivery, input.UserId, actorRole);
        }

        var subject = string.IsNullOrWhiteSpace(input.OrderId)
            ? new GenericCaseSubjectV1 { Type = "account", Ref = input.UserId }
            : new GenericCaseSubjectV1 { Type = "delivery", Ref = input.OrderId };
        var created = await MeasureAsync("create", () => _cases.CreateCaseAsync(
            new CreateGenericCaseRequestV1
            {
                Kind = GenericCaseKinds.Support,
                Category = input.Category.Trim().ToLowerInvariant(),
                Subject = subject,
                RequesterRef = input.UserId,
                ParticipantRefs = new[] { input.UserId },
                Status = GenericCaseStatuses.Open,
                Priority = GenericCasePriorities.Normal,
                Attachments = AttachmentCreates(input.Attachments, null),
            }, input.IdempotencyKey, input.UserId, actorRole, ct), ct, GenericCaseKinds.Support);
        var row = await _cases.GetCaseAsync(created.CaseId, ct);
        var persistedMessages = await _cases.GetCaseMessagesAsync(created.CaseId, includeInternal: true, ct);
        if (!persistedMessages.Any(message => message.MessageType != "internal_note"))
        {
            row = await AddInitialMessageAsync(row, input.IdempotencyKey, input.UserId,
                actorRole, input.Body.Trim(), ct);
        }
        if (!persistedMessages.Any(IsGatewayMetadata))
        {
            var ticketNumber = "SUP-" + row.CaseId.ToString("N")[..8].ToUpperInvariant();
            row = await AddMetadataAsync(row, input.IdempotencyKey, new CaseGatewayMetadataV1
            {
                Subject = NullIfBlank(input.Subject) ?? $"Support: {input.Category}",
                TicketNumber = ticketNumber,
            }, ct);
        }
        row = await _cases.GetCaseAsync(created.CaseId, ct);
        _log.LogInformation(
            "event=case.created case_id={CaseId} case_kind=support actor_id={ActorId} "
            + "version={Version} correlation_id={CorrelationId}",
            row.CaseId, input.UserId, row.Version, CorrelationId());
        CaseTelemetry.Requests.Add(1, new("kind", "support"), new("operation", "create"), new("outcome", "success"));
        return await LoadDetailAsync(row, ct);
    }

    public async Task<GenericCaseDetailV1> GetForUserAsync(
        string caseId, string userId, bool isAdmin, CancellationToken ct)
    {
        var id = ParseCaseId(caseId);
        var row = await MeasureAsync("get", () => _cases.GetCaseAsync(id, ct), ct);
        if (!isAdmin)
        {
            if (row.Kind == GenericCaseKinds.Dispute)
                EnsureParty(await GetDeliveryCaseContextAsync(row.Subject.Ref, ct), userId);
            else if (!string.Equals(row.RequesterRef, userId, StringComparison.Ordinal))
                throw new CaseAccessDeniedException();
        }
        var detail = await LoadDetailAsync(row, ct);
        CaseTelemetry.Requests.Add(1, new("kind", row.Kind), new("operation", "get"), new("outcome", "success"));
        return detail;
    }

    public async Task<GenericCaseDetailV1> GetForRequesterAsync(
        string caseId, string requesterRef, CancellationToken ct)
    {
        var id = ParseCaseId(caseId);
        var row = await MeasureAsync("get_requester", () => _cases.GetCaseAsync(id, ct), ct);
        if (!string.Equals(row.RequesterRef, requesterRef, StringComparison.Ordinal))
            throw new CaseAccessDeniedException();
        var detail = await LoadDetailAsync(row, ct);
        CaseTelemetry.Requests.Add(1,
            new("kind", row.Kind), new("operation", "get_requester"), new("outcome", "success"));
        return detail;
    }

    public async Task<GenericCasePageV1> ListForUserAsync(
        string kind, string userId, GenericCaseQueryV1 query, CancellationToken ct)
    {
        if (kind == GenericCaseKinds.Dispute && !string.IsNullOrWhiteSpace(query.SubjectRef))
        {
            var delivery = await GetDeliveryCaseContextAsync(query.SubjectRef, ct);
            EnsureParty(delivery, userId);
        }

        var rows = await MeasureAsync("list", () => _cases.ListCasesAsync(new GenericCaseQueryV1
        {
            Kind = kind,
            Status = query.Status,
            Priority = query.Priority,
            RequesterRef = null,
            ParticipantRef = userId,
            SubjectType = kind == GenericCaseKinds.Dispute && !string.IsNullOrWhiteSpace(query.SubjectRef)
                ? "delivery" : query.SubjectType,
            SubjectRef = query.SubjectRef,
            DueBefore = query.DueBefore,
            Active = query.Active,
            Sort = GenericCaseSorts.Recent,
            Limit = Math.Clamp(query.Limit, 1, 200),
            Cursor = query.Cursor,
        }, ct), ct);
        CaseTelemetry.Requests.Add(1, new("kind", kind), new("operation", "list"), new("outcome", "success"));
        return rows;
    }

    public async Task<GenericCasePageV1> ListForRequesterAsync(
        string kind, string requesterRef, GenericCaseQueryV1 query, CancellationToken ct)
    {
        var rows = await MeasureAsync("list_requester", () => _cases.ListCasesAsync(new GenericCaseQueryV1
        {
            Kind = kind,
            Status = query.Status,
            Priority = query.Priority,
            RequesterRef = requesterRef,
            ParticipantRef = null,
            SubjectType = query.SubjectType,
            SubjectRef = query.SubjectRef,
            DueBefore = query.DueBefore,
            Active = query.Active,
            Sort = GenericCaseSorts.Recent,
            Limit = Math.Clamp(query.Limit, 1, 200),
            Cursor = query.Cursor,
        }, ct), ct);
        CaseTelemetry.Requests.Add(1,
            new("kind", kind), new("operation", "list_requester"), new("outcome", "success"));
        return rows;
    }

    public async Task<GenericCaseMessagePageV1> ListMessagesForUserAsync(
        string caseId, string userId, bool isAdmin, int limit, string? cursor, CancellationToken ct)
    {
        var id = ParseCaseId(caseId);
        var row = await MeasureAsync("get", () => _cases.GetCaseAsync(id, ct), ct);
        if (!string.Equals(row.Kind, GenericCaseKinds.Support, StringComparison.Ordinal))
            throw new CaseNotFoundException("Case was not found.");
        if (!isAdmin && !row.ParticipantRefs.Contains(userId, StringComparer.Ordinal))
            throw new CaseAccessDeniedException();
        var page = await MeasureAsync("messages_page", () => _cases.GetCaseMessagesPageAsync(
            id, includeInternal: isAdmin, GenericCaseMessageOrders.Newest,
            Math.Clamp(limit, 1, 200), cursor, ct), ct, row.Kind);
        CaseTelemetry.Requests.Add(1,
            new("kind", row.Kind), new("operation", "messages_page"), new("outcome", "success"));
        return page;
    }

    public async Task<DisputeEvidencePreviewResponseV1> PreviewDisputeEvidenceAsync(
        string deliveryId, string userId, string userRole, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
            throw new CaseValidationException("deliveryId is required.");
        var delivery = await GetDeliveryCaseContextAsync(deliveryId, ct);
        ResolvePartyRole(delivery, userId, userRole);
        var evidence = await _evidence.CaptureAsync(deliveryId, userId, Array.Empty<string>(), ct);
        CaseTelemetry.Requests.Add(1,
            new("kind", GenericCaseKinds.Dispute), new("operation", "evidence_preview"), new("outcome", "success"));
        return new DisputeEvidencePreviewResponseV1 { DeliveryId = deliveryId, Evidence = evidence };
    }

    public async Task<GenericCasePageV1> ListAdminAsync(
        GenericCaseQueryV1 query, bool? unassigned, CancellationToken ct)
    {
        if (unassigned == true && !string.IsNullOrWhiteSpace(query.AssigneeRef))
            throw new CaseValidationException("assignedTo and unassigned=true cannot be combined.");
        var upstreamQuery = new GenericCaseQueryV1
        {
            Kind = query.Kind, Status = query.Status, Priority = query.Priority,
            AssigneeRef = query.AssigneeRef,
            Assigned = unassigned is null ? query.Assigned : !unassigned.Value,
            RequesterRef = query.RequesterRef,
            ParticipantRef = query.ParticipantRef,
            SubjectType = query.SubjectType, SubjectRef = query.SubjectRef,
            DueBefore = query.DueBefore, Active = query.Active,
            Sort = GenericCaseSorts.Sla,
            Limit = Math.Clamp(query.Limit, 1, 200), Cursor = query.Cursor,
        };
        return await MeasureAsync("queue", () => _cases.ListCasesAsync(upstreamQuery, ct), ct);
    }

    public async Task<GenericCaseDetailV1> PatchAsync(string caseId, PatchGenericCaseRequestV1 patch,
        string actorId, string actorRole, string idempotencyKey, CancellationToken ct)
    {
        ValidateMutation(patch.ExpectedVersion, idempotencyKey);
        using var activity = CaseTelemetry.Activities.StartActivity("case.patch", ActivityKind.Internal);
        var row = await MeasureAsync("patch", () => _cases.PatchCaseAsync(ParseCaseId(caseId), patch,
            idempotencyKey, actorId, actorRole, ct), ct);
        activity?.SetTag("case.id", row.CaseId);
        activity?.SetTag("case.kind", row.Kind);
        _log.LogInformation(
            "event=case.updated case_id={CaseId} case_kind={CaseKind} actor_id={ActorId} "
            + "version={Version} correlation_id={CorrelationId}",
            row.CaseId, row.Kind, actorId, row.Version, CorrelationId());
        CaseTelemetry.Requests.Add(1, new("kind", row.Kind), new("operation", "patch"), new("outcome", "success"));
        return await LoadDetailAsync(row, ct);
    }

    public async Task<GenericCaseDetailV1> ApplyStatusMessageAsync(string caseId, int expectedVersion,
        string status, string body, string actorId, string actorRole,
        string idempotencyKey, CancellationToken ct)
    {
        ValidateMutation(expectedVersion, idempotencyKey);
        if (string.IsNullOrWhiteSpace(status) || string.IsNullOrWhiteSpace(body))
            throw new CaseValidationException("A status and public message are required.");
        var id = ParseCaseId(caseId);
        using var activity = CaseTelemetry.Activities.StartActivity("case.status_message", ActivityKind.Internal);
        var result = await MeasureAsync("status_message", () => _cases.ApplyCaseStatusMessageAsync(id,
            new ApplyGenericCaseStatusMessageRequestV1
            {
                ExpectedVersion = expectedVersion,
                Status = status,
                Body = body.Trim(),
            }, idempotencyKey, actorId, actorRole, ct), ct);
        activity?.SetTag("case.id", result.Case.CaseId);
        activity?.SetTag("case.kind", result.Case.Kind);
        _log.LogInformation(
            "event=case.status_message_applied case_id={CaseId} case_kind={CaseKind} status={Status} "
            + "actor_id={ActorId} version={Version} correlation_id={CorrelationId}",
            result.Case.CaseId, result.Case.Kind, result.Case.Status, actorId,
            result.Case.Version, CorrelationId());
        CaseTelemetry.Requests.Add(1,
            new("kind", result.Case.Kind), new("operation", "status_message"), new("outcome", "success"));
        return await LoadDetailAsync(result.Case, ct);
    }

    public async Task<GenericCaseDetailV1> AddMessageAsync(string caseId, int expectedVersion,
        string messageType, string actorId, string actorRole, string idempotencyKey,
        string? body, Guid? replyToId, IReadOnlyList<string>? attachments, CancellationToken ct)
    {
        ValidateMutation(expectedVersion, idempotencyKey);
        if (attachments?.Count > MaxAttachments)
            throw new CaseValidationException($"A maximum of {MaxAttachments} attachments is allowed.");
        var id = ParseCaseId(caseId);
        using var activity = CaseTelemetry.Activities.StartActivity("case.message", ActivityKind.Internal);
        var result = await MeasureAsync("message", () => _cases.AddCaseMessageAsync(id,
            new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = expectedVersion, MessageType = messageType,
                Body = NullIfBlank(body), ReplyToId = replyToId,
                Attachments = AttachmentCreates(attachments ?? Array.Empty<string>(), null),
            }, idempotencyKey, actorId, actorRole, ct), ct);
        var row = await _cases.GetCaseAsync(id, ct);
        activity?.SetTag("case.id", row.CaseId);
        activity?.SetTag("case.kind", row.Kind);
        _log.LogInformation(
            "event=case.message_added case_id={CaseId} case_kind={CaseKind} message_type={MessageType} "
            + "actor_id={ActorId} version={Version} correlation_id={CorrelationId}",
            row.CaseId, row.Kind, messageType, actorId, result.CaseVersion, CorrelationId());
        CaseTelemetry.Requests.Add(1, new("kind", row.Kind), new("operation", "message"), new("outcome", "success"));
        return await LoadDetailAsync(row, ct);
    }

    public async Task<GenericCaseDetailV1> ReopenAsync(string caseId, int expectedVersion,
        string actorId, string actorRole, string idempotencyKey, string? reason, CancellationToken ct)
    {
        ValidateMutation(expectedVersion, idempotencyKey);
        using var activity = CaseTelemetry.Activities.StartActivity("case.reopen", ActivityKind.Internal);
        var original = await LoadDetailAsync(
            await MeasureAsync("get", () => _cases.GetCaseAsync(ParseCaseId(caseId), ct), ct), ct);
        if (original.Case.Version != expectedVersion)
            throw new CaseConflictException("The supplied version is stale.");

        var reopenedStatus = original.Case.Kind == GenericCaseKinds.Dispute
            ? GenericCaseStatuses.Pending : GenericCaseStatuses.Open;
        if (original.Case.ClosedAt is null)
        {
            return await PatchAsync(caseId, new PatchGenericCaseRequestV1
            {
                ExpectedVersion = expectedVersion,
                Status = reopenedStatus,
            }, actorId, actorRole, idempotencyKey, ct);
        }

        var reopened = await MeasureAsync("reopen", () => _cases.CreateCaseAsync(
            new CreateGenericCaseRequestV1
            {
                Kind = original.Case.Kind,
                Category = original.Case.Category,
                Subject = original.Case.Subject,
                RequesterRef = original.Case.RequesterRef,
                ParticipantRefs = original.Case.ParticipantRefs,
                Status = reopenedStatus,
                Priority = original.Case.Priority,
                AssigneeRef = original.Case.AssigneeRef,
                DueAt = original.Case.DueAt,
                Attachments = original.Attachments.Select(item => new GenericCaseAttachmentCreateV1
                {
                    CdnRef = item.CdnRef,
                    FileName = item.FileName,
                    ContentType = item.ContentType,
                    SizeBytes = item.SizeBytes,
                }).ToArray(),
            }, idempotencyKey, actorId, actorRole, ct), ct);
        var replacement = await _cases.GetCaseAsync(reopened.CaseId, ct);
        var replacementMessages = await _cases.GetCaseMessagesAsync(
            reopened.CaseId, includeInternal: true, ct);
        var opening = original.Messages.FirstOrDefault(message => message.MessageType != "internal_note");
        if (opening is not null
            && !replacementMessages.Any(message => message.MessageType != "internal_note"))
        {
            var copied = await _cases.AddCaseMessageAsync(reopened.CaseId,
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = replacement.Version,
                    MessageType = "message",
                    Body = opening.Body,
                }, DeterministicKey(idempotencyKey, "reopen-opening"),
                opening.Actor.Ref, opening.Actor.Role, ct);
            replacement = replacement.WithVersion(copied.CaseVersion);
        }

        var metadata = original.Messages.LastOrDefault(IsGatewayMetadata);
        if (metadata is not null && !replacementMessages.Any(IsGatewayMetadata))
        {
            var copied = await _cases.AddCaseMessageAsync(reopened.CaseId,
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = replacement.Version,
                    MessageType = "internal_note",
                    Body = metadata.Body,
                }, DeterministicKey(idempotencyKey, "reopen-metadata"),
                "jeeb-gateway", "system", ct);
            replacement = replacement.WithVersion(copied.CaseVersion);
        }

        var link = JsonSerializer.Serialize(new
        {
            type = "case_reopened",
            predecessorCaseId = original.Case.CaseId,
            reason = NullIfBlank(reason),
        }, Json);
        if (!replacementMessages.Any(message => IsReopenLink(message, original.Case.CaseId)))
        {
            var linked = await _cases.AddCaseMessageAsync(reopened.CaseId,
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = replacement.Version,
                    MessageType = "internal_note",
                    Body = link,
                }, DeterministicKey(idempotencyKey, "reopen-link"), actorId, actorRole, ct);
            replacement = replacement.WithVersion(linked.CaseVersion);
        }
        activity?.SetTag("case.id", reopened.CaseId);
        activity?.SetTag("case.predecessor_id", original.Case.CaseId);
        _log.LogInformation(
            "event=case.reopened case_id={CaseId} predecessor_case_id={PredecessorCaseId} "
            + "actor_id={ActorId} correlation_id={CorrelationId}",
            reopened.CaseId, original.Case.CaseId, actorId, CorrelationId());
        CaseTelemetry.Requests.Add(1, new("kind", reopened.Kind), new("operation", "reopen"), new("outcome", "success"));
        return await LoadDetailAsync(replacement, ct);
    }

    private async Task<GenericCaseV1> AddInitialMessageAsync(GenericCaseV1 row, string rootKey,
        string actorId, string actorRole, string body, CancellationToken ct)
    {
        var result = await _cases.AddCaseMessageAsync(row.CaseId,
            new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = row.Version, MessageType = "message", Body = body,
            }, DeterministicKey(rootKey, "initial-message"), actorId, actorRole, ct);
        return row.WithVersion(result.CaseVersion);
    }

    private async Task<GenericCaseV1> AddMetadataAsync(GenericCaseV1 row, string rootKey,
        CaseGatewayMetadataV1 metadata, CancellationToken ct)
    {
        var body = CaseApiProjection.MetadataBody(metadata);
        var result = await _cases.AddCaseMessageAsync(row.CaseId,
            new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = row.Version, MessageType = "internal_note", Body = body,
            }, DeterministicKey(rootKey, "gateway-metadata"), "jeeb-gateway", "system", ct);
        return row.WithVersion(result.CaseVersion);
    }

    private async Task<GenericCaseV1> ActivateIncidentAsync(GenericCaseV1 row,
        CreateDisputeCaseInput input, string actorRole,
        DeliveryCaseContextUpstream delivery, CancellationToken ct)
    {
        var key = $"case:{row.CaseId:D}:incident:activate";
        var transition = await _delivery.ActivateIncidentAsync(delivery.DeliveryId,
            actorRole, input.UserId, actorRole, key, ct);
        var result = await _cases.AddCaseMessageAsync(row.CaseId,
            new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = row.Version,
                MessageType = "internal_note",
                Body = JsonSerializer.Serialize(new
                {
                    type = "delivery_incident", command = ActivateIncidentCommand,
                    transition.Status, transition.TransitionId,
                }, Json),
            }, DeterministicKey(key, "audit"), "jeeb-gateway", "system", ct);
        return row.WithVersion(result.CaseVersion);
    }

    private async Task<GenericCaseV1> ActivateIncidentSafelyAsync(GenericCaseV1 row,
        CreateDisputeCaseInput input, string actorRole,
        DeliveryCaseContextUpstream delivery, CancellationToken ct)
    {
        try
        {
            return await ActivateIncidentAsync(row, input, actorRole, delivery, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            int? statusCode = error is DeliveryTransitionException transition
                ? transition.StatusCode
                : null;
            CaseTelemetry.SecondaryFailures.Add(1,
                new("kind", GenericCaseKinds.Dispute),
                new("operation", "activate_delivery_incident"),
                new("status_code", statusCode));
            Activity.Current?.SetTag("case.secondary_failure", "activate_delivery_incident");
            Activity.Current?.SetTag("case.secondary_failure.status_code", statusCode);
            _log.LogWarning(error,
                "event=case.secondary_failure case_id={CaseId} case_kind=dispute "
                + "delivery_id={DeliveryId} operation=activate_delivery_incident status_code={StatusCode} "
                + "correlation_id={CorrelationId}; durable case remains authoritative",
                row.CaseId, delivery.DeliveryId, statusCode, CorrelationId());

            // The case has already committed. Record a safe, admin-visible outcome when
            // possible, but never let this optional audit write turn that durable success
            // back into a 5xx either.
            try
            {
                var key = $"case:{row.CaseId:D}:incident:activate";
                var result = await _cases.AddCaseMessageAsync(row.CaseId,
                    new CreateGenericCaseMessageRequestV1
                    {
                        ExpectedVersion = row.Version,
                        MessageType = "internal_note",
                        Body = JsonSerializer.Serialize(new
                        {
                            type = "delivery_incident_activation_failed",
                            command = ActivateIncidentCommand,
                            statusCode,
                            failureType = error.GetType().Name,
                            observedAt = _clock.GetUtcNow(),
                        }, Json),
                    }, DeterministicKey(key, "failure-audit"), "jeeb-gateway", "system", ct);
                return row.WithVersion(result.CaseVersion);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception auditError)
            {
                _log.LogWarning(auditError,
                    "event=case.secondary_failure_audit_failed case_id={CaseId} "
                    + "operation=activate_delivery_incident correlation_id={CorrelationId}; "
                    + "durable case remains authoritative",
                    row.CaseId, CorrelationId());
                return row;
            }
        }
    }

    private async Task<GenericCaseDetailV1> LoadDetailAsync(GenericCaseV1 row, CancellationToken ct)
    {
        var newestMessages = _cases.GetCaseMessagesPageAsync(row.CaseId, includeInternal: true,
            GenericCaseMessageOrders.Newest, DetailMessageLimit, null, ct);
        var openingWindow = _cases.GetCaseMessagesPageAsync(row.CaseId, includeInternal: true,
            GenericCaseMessageOrders.Oldest, MetadataProbeSize, null, ct);
        var attachments = _cases.GetCaseAttachmentsAsync(row.CaseId, ct);
        var audit = _cases.GetCaseAuditAsync(row.CaseId, ct);
        await Task.WhenAll(newestMessages, openingWindow, attachments, audit);
        var oldest = (await openingWindow).Items;
        var anchors = new[]
            {
                oldest.FirstOrDefault(message => message.MessageType != "internal_note"),
                oldest.FirstOrDefault(IsGatewayMetadata),
            }
            .Where(message => message is not null)
            .Cast<GenericCaseMessageV1>()
            .DistinctBy(message => message.MessageId)
            .ToArray();
        var anchorIds = anchors.Select(message => message.MessageId).ToHashSet();
        var newest = (await newestMessages).Items.Where(message => !anchorIds.Contains(message.MessageId));
        var messages = anchors.Concat(newest.TakeLast(DetailMessageLimit - anchors.Length))
            .DistinctBy(message => message.MessageId)
            .OrderBy(message => message.CaseVersion).ThenBy(message => message.MessageId)
            .Take(DetailMessageLimit)
            .ToArray();
        return new GenericCaseDetailV1
        {
            Case = row, Messages = messages, Attachments = await attachments, Audit = await audit,
        };
    }

    private static IReadOnlyList<GenericCaseAttachmentCreateV1>? AttachmentCreates(
        IReadOnlyList<string> refs, string? extra)
    {
        var values = refs.Concat(string.IsNullOrWhiteSpace(extra) ? Array.Empty<string>() : new[] { extra! })
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal).Select(value => new GenericCaseAttachmentCreateV1 { CdnRef = value })
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static bool IsGatewayMetadata(GenericCaseMessageV1 message) =>
        message.MessageType == "internal_note"
        && message.Body.StartsWith(CaseApiProjection.MetadataPrefix, StringComparison.Ordinal);

    private static bool IsReopenLink(GenericCaseMessageV1 message, Guid predecessorCaseId)
    {
        if (message.MessageType != "internal_note" || string.IsNullOrWhiteSpace(message.Body))
            return false;
        try
        {
            using var value = JsonDocument.Parse(message.Body);
            return value.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == "case_reopened"
                && value.RootElement.TryGetProperty("predecessorCaseId", out var predecessor)
                && predecessor.TryGetGuid(out var parsed)
                && parsed == predecessorCaseId;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsDeliveryIncidentOutcomeAudit(GenericCaseMessageV1 message)
    {
        if (message.MessageType != "internal_note" || string.IsNullOrWhiteSpace(message.Body)
            || message.Body[0] != '{') return false;
        try
        {
            using var document = JsonDocument.Parse(message.Body);
            return document.RootElement.TryGetProperty("type", out var type)
                && type.GetString() is "delivery_incident" or "delivery_incident_activation_failed";
        }
        catch (JsonException) { return false; }
    }

    private static IReadOnlyList<GenericCaseEvidenceV1> CompactEvidence(
        IReadOnlyList<GenericCaseEvidenceV1> evidence)
    {
        var candidate = new CaseGatewayMetadataV1 { Evidence = evidence };
        if (CaseApiProjection.MetadataBody(candidate).Length <= MetadataMessageBudget) return evidence;
        return evidence.Select(item => new GenericCaseEvidenceV1
        {
            Source = item.Source,
            Status = item.Status == "complete" ? "partial" : item.Status,
            CapturedAt = item.CapturedAt,
            Count = item.Count,
            RetentionDays = item.RetentionDays,
            ExpiresAt = item.ExpiresAt,
            Marker = item.Marker ?? "payload_omitted_case_message_limit",
        }).ToArray();
    }

    private async Task<DeliveryCaseContextUpstream> GetDeliveryCaseContextAsync(string id, CancellationToken ct) =>
        await _delivery.GetDeliveryCaseContextAsync(id, ct) ??
        throw new CaseNotFoundException($"Delivery '{id}' was not found.");

    private static void EnsureParty(DeliveryCaseContextUpstream delivery, string userId)
    {
        if (!IsParty(delivery, userId))
            throw new CaseAccessDeniedException();
    }

    private static bool IsParty(DeliveryCaseContextUpstream delivery, string userId) =>
        string.Equals(delivery.PartyIds.ClientId, userId, StringComparison.Ordinal)
        || string.Equals(delivery.PartyIds.CourierId, userId, StringComparison.Ordinal);

    private static string ResolvePartyRole(
        DeliveryCaseContextUpstream delivery, string userId, string assertedRole)
    {
        EnsureEndUserRole(assertedRole);
        if (delivery.PartyIds.ClientId == userId) return "client";
        if (delivery.PartyIds.CourierId == userId) return "jeeber";
        throw new CaseAccessDeniedException();
    }

    private static string EnsureEndUserRole(string role) => role switch
    {
        "client" => "client",
        "jeeber" => "jeeber",
        _ => throw new CaseAccessDeniedException(),
    };

    private static Guid ParseCaseId(string value)
    {
        var candidate = value.StartsWith("dsp_", StringComparison.OrdinalIgnoreCase)
            ? value[4..] : value;
        return Guid.TryParse(candidate, out var id)
            ? id : throw new CaseNotFoundException("Case was not found.");
    }

    private static void ValidateMutation(int version, string key)
    {
        if (version < 1) throw new CaseValidationException("expectedVersion must be at least 1.");
        if (string.IsNullOrWhiteSpace(key)) throw new CaseValidationException("Idempotency-Key is required.");
    }

    private static void ValidateDispute(CreateDisputeCaseInput input)
    {
        if (string.IsNullOrWhiteSpace(input.DeliveryId)) throw new CaseValidationException("deliveryId is required.");
        if (string.IsNullOrWhiteSpace(input.Reason)
            || !DisputeReasons.Contains(input.Reason.Trim().ToLowerInvariant()))
            throw new CaseValidationException("reason is required and must be a supported dispute reason.");
        if (input.Attachments.Count > MaxAttachments) throw new CaseValidationException("A maximum of 5 attachments is allowed.");
        if (!string.IsNullOrWhiteSpace(input.IncidentCommand)
            && !string.Equals(input.IncidentCommand, ActivateIncidentCommand, StringComparison.OrdinalIgnoreCase))
            throw new CaseValidationException($"Unknown incidentCommand '{input.IncidentCommand}'.");
    }

    private static void ValidateSupport(CreateSupportCaseInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Category)) throw new CaseValidationException("category is required.");
        if (string.IsNullOrWhiteSpace(input.Body)) throw new CaseValidationException("A non-empty ticket body is required.");
        if (input.Attachments.Count > MaxAttachments) throw new CaseValidationException("A maximum of 5 attachments is allowed.");
    }

    private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action,
        CancellationToken ct, string? kind = null)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await action(); }
        catch (GenericCaseApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            throw new CaseNotFoundException("Case was not found.");
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
        {
            throw new CaseConflictException(ex.ResponseBody, kind);
        }
        catch (GenericCaseApiException)
        {
            throw;
        }
        finally
        {
            CaseTelemetry.UpstreamDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", operation));
        }
    }

    public static string DeterministicKey(params string?[] values)
    {
        var canonical = string.Join("\n", values.Select(value => value?.Trim() ?? string.Empty));
        return "case:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CanonicalParticipantRefs(DeliveryCaseContextUpstream delivery) =>
        new[] { delivery.PartyIds.ClientId, delivery.PartyIds.CourierId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string CorrelationId() => Activity.Current?.TraceId.ToString() ?? "none";

    private static string NormalizeDisputeReason(string value) => value.Trim().ToLowerInvariant() switch
    {
        "damaged_goods" => "damaged",
        "wrong_delivery" => "wrong_item",
        "no_delivery" => "no_show",
        var reason => reason,
    };
}

internal static class GenericCaseVersionCopy
{
    public static GenericCaseV1 WithVersion(this GenericCaseV1 row, int version) => new()
    {
        CaseId = row.CaseId, Kind = row.Kind, Category = row.Category, Subject = row.Subject,
        RequesterRef = row.RequesterRef, ParticipantRefs = row.ParticipantRefs,
        Status = row.Status, Priority = row.Priority,
        AssigneeRef = row.AssigneeRef, DueAt = row.DueAt, Version = version,
        ClosedAt = row.ClosedAt, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
    };
}

public sealed class CaseValidationException(string message) : Exception(message);
public sealed class CaseAccessDeniedException : Exception;
public sealed class CaseNotFoundException(string message) : Exception(message);
public sealed class CaseConflictException(string? detail, string? kind = null)
    : Exception("The case changed concurrently or already exists.")
{
    public string? Detail { get; } = detail;
    public string? ExistingCaseId { get; } = ReadString(detail, "existingCaseId")
        ?? ReadString(detail, "caseId");
    public string? Kind { get; } = kind ?? ReadString(detail, "kind");

    private static string? ReadString(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (document.RootElement.TryGetProperty(property, out var direct)) return direct.GetString();
            return document.RootElement.TryGetProperty("extensions", out var extensions)
                && extensions.ValueKind == JsonValueKind.Object
                && extensions.TryGetProperty(property, out var nested)
                ? nested.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
