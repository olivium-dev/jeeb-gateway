using System.Text.Json;

namespace JeebGateway.Cases;

public sealed class CreateDisputeRequestV2
{
    public string? DeliveryId { get; init; }
    public string? RequestId { get; init; }
    public string? Reason { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string>? Photos { get; init; }
    public IReadOnlyList<string>? PhotoUrls { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
    public string? VoiceUrl { get; init; }
    public string? IncidentCommand { get; init; }
    public JsonElement? Evidence { get; init; }

    public string? ResolveDeliveryId(string? routeDeliveryId = null) =>
        First(routeDeliveryId, DeliveryId, RequestId);

    public IReadOnlyList<string> ResolveAttachments()
    {
        var voice = VoiceUrl?.Trim();
        return (Attachments ?? Photos ?? PhotoUrls ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Where(value => !string.Equals(value, voice, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed class CaseReplyRequestV2
{
    public string? Body { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
    public Guid? ReplyToId { get; init; }
    public long? ExpectedVersion { get; init; }
}

public sealed class CaseClaimRequestV1 { public long? ExpectedVersion { get; init; } }
public sealed class CaseReassignRequestV1
{
    public string? AssigneeUserId { get; init; }
    public long? ExpectedVersion { get; init; }
}
public sealed class CasePriorityRequestV1
{
    public string? Priority { get; init; }
    public long? ExpectedVersion { get; init; }
}
public sealed class CaseDueRequestV1
{
    public DateTimeOffset? DueAt { get; init; }
    public bool Clear { get; init; }
    public long? ExpectedVersion { get; init; }
}
public sealed class CaseNoteRequestV1
{
    public string? Body { get; init; }
    public long? ExpectedVersion { get; init; }
}
public sealed class CaseStatusRequestV1
{
    public string? Reason { get; init; }
    public long? ExpectedVersion { get; init; }
}

public sealed class CaseDetailResponseV2
{
    public string SchemaVersion { get; init; } = "2";
    public required string Id { get; init; }
    public string? DisputeId { get; init; }
    public string? TicketId { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public required string State { get; init; }
    public required string Priority { get; init; }
    public string? AssignedTo { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public string? RequesterRef { get; init; }
    /// <summary>
    /// Canonical case participants from the state owner. Admin detail uses this
    /// to identify every party without inferring them from messages.
    /// </summary>
    public IReadOnlyList<string> ParticipantRefs { get; init; } = Array.Empty<string>();
    public string? UserId { get; init; }
    public string? DeliveryId { get; init; }
    public string? RequestId { get; init; }
    public string? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? TicketNumber { get; init; }
    public string? Category { get; init; }
    public required string Subject { get; init; }
    public required string Description { get; init; }
    public string? Body { get; init; }
    public string? Reason { get; init; }
    public string? Comment { get; init; }
    public string? VoiceUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
    public string? IncidentCommand { get; init; }
    public IReadOnlyList<GenericCaseEvidenceV1> Evidence { get; init; }
        = Array.Empty<GenericCaseEvidenceV1>();
    public IReadOnlyList<GenericCaseMessageV1> Messages { get; init; }
        = Array.Empty<GenericCaseMessageV1>();
    public IReadOnlyList<GenericCaseAuditEventV1> Timeline { get; init; }
        = Array.Empty<GenericCaseAuditEventV1>();
    public long Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? FixedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

public sealed class CasePageResponseV2
{
    public string SchemaVersion { get; init; } = "2";
    public IReadOnlyList<CaseDetailResponseV2> Items { get; init; }
        = Array.Empty<CaseDetailResponseV2>();
    public int Total { get; init; }
    public int TotalCount => Total;
    public string? NextCursor { get; init; }
    public string? Cursor => NextCursor;
}

public sealed class CaseMessagePageResponseV2
{
    public string SchemaVersion { get; init; } = "2";
    // The page contains the newest available window in chronological display
    // order. nextCursor walks strictly earlier messages for prepend-on-load.
    public IReadOnlyList<GenericCaseMessageV1> Items { get; init; }
        = Array.Empty<GenericCaseMessageV1>();
    public int Total { get; init; }
    public string? NextCursor { get; init; }
    public string? Cursor => NextCursor;
}

public sealed class DisputeEvidencePreviewResponseV1
{
    public string SchemaVersion { get; init; } = "1";
    public required string DeliveryId { get; init; }
    public IReadOnlyList<GenericCaseEvidenceV1> Evidence { get; init; }
        = Array.Empty<GenericCaseEvidenceV1>();
}

public sealed class CreateDisputeCaseInput
{
    public required string DeliveryId { get; init; }
    public required string UserId { get; init; }
    public required string UserRole { get; init; }
    public required string Reason { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
    public string? VoiceUrl { get; init; }
    public string? IncidentCommand { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed class CreateSupportCaseInput
{
    public required string UserId { get; init; }
    public required string UserRole { get; init; }
    public required string Category { get; init; }
    public string? Subject { get; init; }
    public required string Body { get; init; }
    public string? OrderId { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
    public required string IdempotencyKey { get; init; }
}

public static class CaseApiProjection
{
    public const string MetadataPrefix = "__jeeb_gateway_metadata_v1__:";

    public static CaseDetailResponseV2 Project(GenericCaseDetailV1 detail, bool includeInternal)
    {
        var row = detail.Case;
        var metadata = ReadMetadata(detail.Messages);
        var publicMessages = detail.Messages
            .Where(message => !IsSyntheticMetadataMessage(message)
                              && (includeInternal || message.MessageType != "internal_note"))
            .ToArray();
        var timeline = detail.Audit
            .Where(item => includeInternal || !IsInternalNoteAudit(item))
            .ToArray();
        var firstPublic = detail.Messages.FirstOrDefault(message => message.MessageType != "internal_note");
        var refs = detail.Attachments.Select(item => item.CdnRef).Distinct(StringComparer.Ordinal).ToArray();
        var photoRefs = refs.Where(item => !string.Equals(item, metadata?.VoiceUrl, StringComparison.Ordinal)).ToArray();
        var subject = metadata?.Subject ?? (row.Kind == GenericCaseKinds.Dispute
            ? $"Delivery dispute: {row.Category}" : $"Support: {row.Category}");
        var body = firstPublic?.Body ?? string.Empty;
        return new CaseDetailResponseV2
        {
            Id = row.CaseId.ToString("D"),
            DisputeId = row.Kind == GenericCaseKinds.Dispute ? row.CaseId.ToString("D") : null,
            TicketId = row.Kind == GenericCaseKinds.Support ? row.CaseId.ToString("D") : null,
            Kind = row.Kind, Status = row.Status, State = row.Status, Priority = row.Priority,
            AssignedTo = row.AssigneeRef, DueAt = row.DueAt, RequesterRef = row.RequesterRef,
            ParticipantRefs = row.ParticipantRefs,
            UserId = row.RequesterRef,
            DeliveryId = row.Subject.Type == "delivery" ? row.Subject.Ref : null,
            RequestId = row.Kind == GenericCaseKinds.Dispute ? row.Subject.Ref : null,
            OrderId = row.Kind == GenericCaseKinds.Support && row.Subject.Type == "delivery" ? row.Subject.Ref : null,
            OrderRef = row.Kind == GenericCaseKinds.Support && row.Subject.Type == "delivery" ? row.Subject.Ref : null,
            TicketNumber = metadata?.TicketNumber,
            Category = row.Category, Subject = subject, Description = body, Body = row.Kind == GenericCaseKinds.Support ? body : null,
            Reason = row.Kind == GenericCaseKinds.Dispute ? row.Category : null,
            Comment = row.Kind == GenericCaseKinds.Dispute ? body : null,
            VoiceUrl = metadata?.VoiceUrl, Photos = row.Kind == GenericCaseKinds.Dispute ? photoRefs : Array.Empty<string>(),
            Attachments = refs, IncidentCommand = metadata?.IncidentCommand,
            Evidence = metadata?.Evidence ?? Array.Empty<GenericCaseEvidenceV1>(),
            Messages = publicMessages, Timeline = timeline, Version = row.Version,
            CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
            FixedAt = detail.Audit.LastOrDefault(item =>
                item.Data.ValueKind == JsonValueKind.Object
                && item.Data.TryGetProperty("status", out var status)
                && status.GetString() == GenericCaseStatuses.Fixed)?.CreatedAt,
            ClosedAt = row.ClosedAt,
        };
    }

    public static CaseDetailResponseV2 Project(GenericCaseV1 row) => Project(new GenericCaseDetailV1
    {
        Case = row,
    }, false);

    public static CasePageResponseV2 Project(
        GenericCasePageV1 page, string? nextCursor = null, int? total = null) => new()
    {
        Items = page.Items.Select(Project).ToArray(), Total = total ?? page.Total,
        NextCursor = nextCursor ?? page.NextCursor,
    };

    public static string MetadataBody(CaseGatewayMetadataV1 metadata) =>
        MetadataPrefix + JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CaseGatewayMetadataV1? ReadMetadata(IReadOnlyList<GenericCaseMessageV1> messages)
    {
        var body = messages.LastOrDefault(message => message.MessageType == "internal_note"
            && message.Body.StartsWith(MetadataPrefix, StringComparison.Ordinal))?.Body;
        if (body is null) return null;
        try
        {
            return JsonSerializer.Deserialize<CaseGatewayMetadataV1>(body[MetadataPrefix.Length..],
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException) { return null; }
    }

    private static bool IsInternalNoteAudit(GenericCaseAuditEventV1 item) =>
        item.EventType == "case.message_added"
        && item.Data.ValueKind == JsonValueKind.Object
        && item.Data.TryGetProperty("messageType", out var messageType)
        && messageType.GetString() == "internal_note";

    internal static bool IsSyntheticMetadataMessage(GenericCaseMessageV1 message) =>
        message.MessageType == "internal_note"
        && message.Body.StartsWith(MetadataPrefix, StringComparison.Ordinal);
}

public sealed class CaseGatewayMetadataV1
{
    public string SchemaVersion { get; init; } = "1";
    public string? Subject { get; init; }
    public string? TicketNumber { get; init; }
    public string? VoiceUrl { get; init; }
    public string? IncidentCommand { get; init; }
    public IReadOnlyList<GenericCaseEvidenceV1> Evidence { get; init; }
        = Array.Empty<GenericCaseEvidenceV1>();
}
