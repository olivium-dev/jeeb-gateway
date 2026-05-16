using System.Collections.Concurrent;

namespace JeebGateway.Push;

public interface IDeviceTokenStore
{
    Task<IReadOnlyList<DeviceToken>> GetForUserAsync(string userId, CancellationToken ct);
    Task RegisterAsync(DeviceToken token, CancellationToken ct);
    Task UnregisterAsync(string userId, string token, CancellationToken ct);
}

public sealed class InMemoryDeviceTokenStore : IDeviceTokenStore
{
    private readonly ConcurrentDictionary<string, List<DeviceToken>> _byUser = new();
    private readonly object _lock = new();

    public Task<IReadOnlyList<DeviceToken>> GetForUserAsync(string userId, CancellationToken ct)
    {
        if (_byUser.TryGetValue(userId, out var tokens))
        {
            lock (_lock) return Task.FromResult<IReadOnlyList<DeviceToken>>(tokens.ToArray());
        }
        return Task.FromResult<IReadOnlyList<DeviceToken>>(Array.Empty<DeviceToken>());
    }

    public Task RegisterAsync(DeviceToken token, CancellationToken ct)
    {
        lock (_lock)
        {
            var list = _byUser.GetOrAdd(token.UserId, _ => new List<DeviceToken>());
            // Idempotent registration — same (token, platform) is deduplicated.
            if (!list.Any(t => t.Token == token.Token && t.Platform == token.Platform))
            {
                list.Add(token);
            }
        }
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string userId, string token, CancellationToken ct)
    {
        if (_byUser.TryGetValue(userId, out var list))
        {
            lock (_lock) list.RemoveAll(t => t.Token == token);
        }
        return Task.CompletedTask;
    }
}
