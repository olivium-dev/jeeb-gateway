namespace JeebGateway.Services.Dispatch;

/// <summary>
/// A single notification dispatch job stored in the outbox.
/// </summary>
public sealed class NotificationDispatchEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TemplateKey { get; init; }
    public required string Locale { get; init; }
    public required Dictionary<string, string> Parameters { get; init; }
    public required Guid RecipientUserId { get; init; }
    public string? IdempotencyKey { get; init; }
    public NotificationDispatchStatus Status { get; set; } = NotificationDispatchStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

public enum NotificationDispatchStatus
{
    Pending,
    Delivered,
    DLQ
}
