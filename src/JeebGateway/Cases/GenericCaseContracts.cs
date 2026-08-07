using System.Text.Json;

namespace JeebGateway.Cases;

public static class GenericCaseKinds
{
    public const string Dispute = "dispute";
    public const string Support = "support";
}

public static class GenericCaseStatuses
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string Fixed = "fixed";
    public const string Closed = "closed";
}

public static class GenericCasePriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Urgent = "urgent";

    public static bool IsValid(string? value) => value is Low or Normal or High or Urgent;
}

public static class GenericCaseSorts
{
    public const string Recent = "recent";
    public const string Sla = "sla";
}

public static class GenericCaseMessageOrders
{
    public const string Newest = "newest";
    public const string Oldest = "oldest";
}

// Handwritten mirror of jeeb-state-service's canonical /v1/cases API.
public sealed class GenericCaseSubjectV1
{
    public required string Type { get; init; }
    public required string Ref { get; init; }
}

public sealed class GenericCaseAttachmentCreateV1
{
    public required string CdnRef { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed class CreateGenericCaseRequestV1
{
    public required string Kind { get; init; }
    public required string Category { get; init; }
    public required GenericCaseSubjectV1 Subject { get; init; }
    public required string RequesterRef { get; init; }
    public IReadOnlyList<string> ParticipantRefs { get; init; } = Array.Empty<string>();
    public required string Status { get; init; }
    public string Priority { get; init; } = GenericCasePriorities.Normal;
    public string? AssigneeRef { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public IReadOnlyList<GenericCaseAttachmentCreateV1>? Attachments { get; init; }
}

public sealed class PatchGenericCaseRequestV1
{
    public required int ExpectedVersion { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? AssigneeRef { get; init; }
    public bool ClearAssignee { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool ClearDueAt { get; init; }
}

public sealed class ApplyGenericCaseStatusMessageRequestV1
{
    public required int ExpectedVersion { get; init; }
    public required string Status { get; init; }
    public required string Body { get; init; }
}

public sealed class CreateGenericCaseMessageRequestV1
{
    public required int ExpectedVersion { get; init; }
    public required string MessageType { get; init; }
    public string? Body { get; init; }
    public Guid? ReplyToId { get; init; }
    public IReadOnlyList<GenericCaseAttachmentCreateV1>? Attachments { get; init; }
}

public sealed class GenericCaseV1
{
    public required Guid CaseId { get; init; }
    public required string Kind { get; init; }
    public required string Category { get; init; }
    public required GenericCaseSubjectV1 Subject { get; init; }
    public required string RequesterRef { get; init; }
    public IReadOnlyList<string> ParticipantRefs { get; init; } = Array.Empty<string>();
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public string? AssigneeRef { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int Version { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class GenericCaseActorV1
{
    public required string Ref { get; init; }
    public required string Role { get; init; }
}

public sealed class GenericCaseAttachmentV1
{
    public required Guid AttachmentId { get; init; }
    public required Guid CaseId { get; init; }
    public Guid? MessageId { get; init; }
    public required string CdnRef { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public long? SizeBytes { get; init; }
    public required string AddedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class GenericCaseMessageV1
{
    public required Guid MessageId { get; init; }
    public required Guid CaseId { get; init; }
    public required string MessageType { get; init; }
    public Guid? ReplyToId { get; init; }
    public required string Body { get; init; }
    public required GenericCaseActorV1 Actor { get; init; }
    public int CaseVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<GenericCaseAttachmentV1> Attachments { get; init; }
        = Array.Empty<GenericCaseAttachmentV1>();
}

public sealed class GenericCaseMessageCreatedV1
{
    public required GenericCaseMessageV1 Message { get; init; }
    public int CaseVersion { get; init; }
}

public sealed class GenericCaseStatusMessageV1
{
    public required GenericCaseV1 Case { get; init; }
    public required GenericCaseMessageV1 Message { get; init; }
}

public sealed class GenericCaseAuditEventV1
{
    public required Guid EventId { get; init; }
    public required Guid CaseId { get; init; }
    public required string EventType { get; init; }
    public required GenericCaseActorV1 Actor { get; init; }
    public int CaseVersion { get; init; }
    public JsonElement Data { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class GenericCaseDetailV1
{
    public required GenericCaseV1 Case { get; init; }
    public IReadOnlyList<GenericCaseMessageV1> Messages { get; init; }
        = Array.Empty<GenericCaseMessageV1>();
    public IReadOnlyList<GenericCaseAttachmentV1> Attachments { get; init; }
        = Array.Empty<GenericCaseAttachmentV1>();
    public IReadOnlyList<GenericCaseAuditEventV1> Audit { get; init; }
        = Array.Empty<GenericCaseAuditEventV1>();
}

public sealed class GenericCaseQueryV1
{
    public string? Query { get; init; }
    public string? Kind { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? AssigneeRef { get; init; }
    public bool? Assigned { get; init; }
    public string? RequesterRef { get; init; }
    public string? ParticipantRef { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectRef { get; init; }
    public DateTimeOffset? DueBefore { get; init; }
    public bool? Active { get; init; }
    public string? Sort { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
}

public sealed class GenericCasePageV1
{
    public IReadOnlyList<GenericCaseV1> Items { get; init; } = Array.Empty<GenericCaseV1>();
    public string? NextCursor { get; init; }
    public int Total => Items.Count;
}

public sealed class GenericCaseMessagePageV1
{
    public IReadOnlyList<GenericCaseMessageV1> Items { get; init; }
        = Array.Empty<GenericCaseMessageV1>();
    public string? NextCursor { get; init; }
}

public sealed class GenericCaseDeadLetterV1
{
    public Guid EventId { get; init; }
    public Guid CaseId { get; init; }
    public required string EventType { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset DeadLetteredAt { get; init; }
}

public sealed class GenericCaseDeadLetterPageV1
{
    public IReadOnlyList<GenericCaseDeadLetterV1> Items { get; init; }
        = Array.Empty<GenericCaseDeadLetterV1>();
    public string? NextCursor { get; init; }
}

public sealed class GenericCaseDeadLetterRequeueV1
{
    public Guid EventId { get; init; }
    public DateTimeOffset RequeuedAt { get; init; }
    public bool AlreadyRequeued { get; init; }
}

public sealed class GenericCaseCallbackV1
{
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required GenericCaseV1 Case { get; init; }
    public required GenericCaseActorV1 Actor { get; init; }
    public JsonElement Data { get; init; }
}

// Gateway-only evidence envelope serialized into a prefixed internal note.
public sealed class GenericCaseEvidenceV1
{
    public required string Source { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public int? Count { get; init; }
    public int? RetentionDays { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Marker { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed class GenericCaseApiException : Exception
{
    public GenericCaseApiException(int statusCode, string? responseBody)
        : base($"jeeb-state-service generic case call failed with {statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int StatusCode { get; }
    public string? ResponseBody { get; }
}
