using System.Text.Json;

namespace JeebGateway.StateService.Ownership;

// Mirrors JeebStateService.Models.OwnershipContracts (/v1/audit-events, /v1/work-items).
// Payloads are opaque JSON to the gateway; artifact bytes never travel through these DTOs.
public sealed class AuditEventAppendRequestV1
{
    public required string Application { get; init; }
    public required string ActorRef { get; init; }
    public required string ActorRole { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceRef { get; init; }
    public string? RequestId { get; init; }
    public JsonElement? Before { get; init; }
    public JsonElement? After { get; init; }
    public JsonElement? Metadata { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
}

public sealed class AuditEventRecordV1
{
    public Guid EventId { get; init; }
    public string Application { get; init; } = string.Empty;
    public string ActorRef { get; init; } = string.Empty;
    public string ActorRole { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceRef { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public JsonElement Before { get; init; }
    public JsonElement After { get; init; }
    public JsonElement Metadata { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class AuditEventPageV1
{
    public IReadOnlyList<AuditEventRecordV1> Items { get; init; } = Array.Empty<AuditEventRecordV1>();
    public string? NextCursor { get; init; }
}

// Filters for GET /v1/audit-events. application is required and scope-checked by the service.
public sealed class AuditEventQueryV1
{
    public required string Application { get; init; }
    public string? ActorRef { get; init; }
    public string? Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceRef { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
}

public sealed class WorkItemCreateRequestV1
{
    public required string Application { get; init; }
    public required string Kind { get; init; }
    public required string SubjectRef { get; init; }
    public JsonElement? Payload { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int? MaxAttempts { get; init; }
    public DateTimeOffset? RetainPayloadUntil { get; init; }
}

// W1-09 — claim/fail bodies for the durable due-work rail. W1-06 adds complete/consume:
// these routes take no Idempotency-Key — lease token (or token hash) plus version CAS is the control.
public sealed class WorkClaimRequestV1
{
    public required string Application { get; init; }
    public required string WorkerId { get; init; }
    public IReadOnlyList<string>? Kinds { get; init; }
    public int? LeaseSeconds { get; init; }
    public int? Limit { get; init; }
}

public sealed class WorkCompleteRequestV1
{
    public required Guid LeaseToken { get; init; }
    public required int ExpectedVersion { get; init; }
    public JsonElement? Result { get; init; }
    public string? ArtifactRef { get; init; }
    public DateTimeOffset? ArtifactExpiresAt { get; init; }

    // G-20 — only the DERIVED hash travels; the raw download token never leaves the gateway.
    public string? DownloadTokenHash { get; init; }
}

public sealed class WorkFailRequestV1
{
    public required Guid LeaseToken { get; init; }
    public required int ExpectedVersion { get; init; }
    public required string Error { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
}

public sealed class WorkConsumeRequestV1
{
    public required string Application { get; init; }
    public required string DownloadTokenHash { get; init; }
    public required int ExpectedVersion { get; init; }
}

// DownloadTokenHash is [JsonIgnore] server-side and is deliberately not modelled here.
public sealed class WorkItemRecordV1
{
    public Guid WorkItemId { get; init; }
    public string Application { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string SubjectRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
    public JsonElement Result { get; init; }
    public string? ArtifactRef { get; init; }
    public DateTimeOffset? ArtifactExpiresAt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public int Version { get; init; }
    public Guid? LeaseToken { get; init; }
    public string? LeasedBy { get; init; }
    public DateTimeOffset? LeasedUntil { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? RetainPayloadUntil { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
