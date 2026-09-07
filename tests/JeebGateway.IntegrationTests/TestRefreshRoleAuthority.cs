using JeebGateway.Tokens;

namespace JeebGateway.IntegrationTests;

// Explicit test-owned authority. Production never registers this fixture.
internal sealed class TestRefreshRoleAuthority : IRefreshRoleAuthority
{
    private readonly Func<string, CancellationToken, Task<TokenRoleContext?>> _resolve;
    public TestRefreshRoleAuthority(IUsersStoreAdapter users) => _resolve = async (id, ct) =>
    {
        var roles = await users.GetRolesAsync(id, ct);
        var active = await users.GetActiveRoleAsync(id, ct);
        return roles.Count > 0 && roles.Contains(active)
            ? new TokenRoleContext(roles, active) : null;
    };
    public TestRefreshRoleAuthority(IReadOnlyList<string> roles, string active) =>
        _resolve = (_, _) => Task.FromResult<TokenRoleContext?>(
            roles.Count > 0 && roles.Contains(active) ? new TokenRoleContext(roles, active) : null);
    public async Task<RefreshRoleAuthorityResult> ResolveAsync(string userId, CancellationToken ct) =>
        RefreshRoleAuthorityResult.FromContext(await _resolve(userId, ct));
    public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);
}
