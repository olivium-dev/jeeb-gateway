namespace JeebGateway.Users.Moderation;

/// <summary>
/// gwdbx W4-04 — one suspension flip, queued for a best-effort write-through to
/// user-management. The gateway's users projection stays authoritative until
/// the O5-gated cutover; a mirror gap is replayed by the W4-05 backfill.
/// </summary>
public interface IUserModerationMirror
{
    /// <summary>Non-blocking enqueue; must never throw into the admin path.</summary>
    Task MirrorAsync(UserModerationChange change, CancellationToken ct);
}

/// <summary>Wire-agnostic snapshot of one suspend/unsuspend decision.</summary>
public sealed class UserModerationChange
{
    public required string UserId { get; init; }

    public required bool IsSuspended { get; init; }

    public string? Reason { get; init; }

    public string? ActorRef { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Test/off-mode stand-in: accepts and discards every change.</summary>
public sealed class NoOpUserModerationMirror : IUserModerationMirror
{
    public Task MirrorAsync(UserModerationChange change, CancellationToken ct) => Task.CompletedTask;
}
