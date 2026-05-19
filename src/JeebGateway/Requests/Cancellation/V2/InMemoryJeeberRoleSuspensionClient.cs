using System.Collections.Concurrent;

namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// MVP in-memory <see cref="IJeeberRoleSuspensionClient"/>. One row per
/// userId holding the active suspension. <see cref="SuspendAsync"/>
/// overwrites any earlier expiry so a fresh trigger always extends to a
/// full window from now (matches the spec: "suspension of Jeeber role").
///
/// Production wiring will be an HTTP adapter over the NSwag-generated
/// user-management client, but the contract surface is identical so the
/// matching layer and the policy service depend only on the interface.
/// </summary>
public sealed class InMemoryJeeberRoleSuspensionClient : IJeeberRoleSuspensionClient
{
    private readonly ConcurrentDictionary<string, JeeberRoleSuspensionResult> _suspensions =
        new(StringComparer.Ordinal);

    /// <summary>Snapshot of currently-recorded suspensions (test helper).</summary>
    public IReadOnlyCollection<JeeberRoleSuspensionResult> Snapshot => _suspensions.Values.ToArray();

    public Task<JeeberRoleSuspensionResult> SuspendAsync(
        string userId,
        DateTimeOffset at,
        TimeSpan duration,
        string reason,
        CancellationToken ct)
    {
        var record = new JeeberRoleSuspensionResult(userId, at, at + duration, reason);
        _suspensions[userId] = record;
        return Task.FromResult(record);
    }

    public Task<bool> IsSuspendedAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        if (!_suspensions.TryGetValue(userId, out var record))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(at < record.ExpiresAt);
    }

    public Task<DateTimeOffset?> GetSuspensionExpiryAsync(
        string userId, DateTimeOffset at, CancellationToken ct)
    {
        if (!_suspensions.TryGetValue(userId, out var record) || at >= record.ExpiresAt)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }
        return Task.FromResult<DateTimeOffset?>(record.ExpiresAt);
    }
}
