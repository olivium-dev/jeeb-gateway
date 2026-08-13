namespace JeebGateway.Availability;

/// <summary>
/// gwdbx W3-04 — best-effort write-through of the two availability signals the
/// gateway did NOT already forward to delivery-service: the activity watermark
/// and the sweeper's idle flip. Toggle / GET / heartbeat already write through
/// synchronously in the controllers; this seam closes the remainder.
///
/// <para><b>G-10.</b> This mirror only REPORTS a decision the gateway already
/// made. delivery-service never decides presence, so there is still exactly one
/// presence authority.</para>
/// </summary>
public interface IAvailabilityMirror
{
    /// <summary>Reports in-app activity. Never changes the online flag upstream.</summary>
    Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct);

    /// <summary>Reports the gateway sweeper's idle flip. Not user activity.</summary>
    Task MirrorIdleOfflineAsync(string userId, CancellationToken ct);
}

/// <summary>Used when delivery-service is unwired; every call is a no-op.</summary>
public sealed class NoOpAvailabilityMirror : IAvailabilityMirror
{
    public Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

    public Task MirrorIdleOfflineAsync(string userId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Fail-open BY CONSTRUCTION: call sites resolve THIS, never the raw
/// <see cref="IAvailabilityMirror"/>, so even a synchronous throw from the inner
/// mirror cannot reach the availability path or the sweeper.
/// </summary>
public sealed class FailOpenAvailabilityMirror : IAvailabilityMirror
{
    private readonly IAvailabilityMirror _inner;
    private readonly ILogger<FailOpenAvailabilityMirror> _log;

    public FailOpenAvailabilityMirror(IAvailabilityMirror inner, ILogger<FailOpenAvailabilityMirror> log)
    {
        _inner = inner;
        _log = log;
    }

    public Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct) =>
        Guard(() => _inner.MirrorInteractionAsync(userId, at, ct), userId, "interaction");

    public Task MirrorIdleOfflineAsync(string userId, CancellationToken ct) =>
        Guard(() => _inner.MirrorIdleOfflineAsync(userId, ct), userId, "idle-offline");

    private Task Guard(Func<Task> call, string userId, string label)
    {
        try
        {
            _ = call();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "availability mirror ({Label}) threw for jeeber {JeeberId}; the gateway store stays authoritative.",
                label, userId);
        }

        return Task.CompletedTask;
    }
}
