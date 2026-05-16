using JeebGateway.Users;

namespace JeebGateway.Tokens;

public class UsersStoreRolesAdapter : IUsersStoreAdapter
{
    private readonly IUsersStore _store;

    public UsersStoreRolesAdapter(IUsersStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct)
    {
        var profile = await _store.GetByIdAsync(userId, ct);
        return profile?.Roles ?? new List<string>();
    }

    public async Task<string> GetActiveRoleAsync(string userId, CancellationToken ct)
    {
        var profile = await _store.GetByIdAsync(userId, ct);
        return profile?.ActiveRole ?? Roles.Client;
    }
}
