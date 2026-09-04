using JeebGateway.Users;

namespace JeebGateway.Health;

/// <summary>How many profiles the local users store holds. Narrow on purpose: the readiness row
/// needs one number, not the whole <see cref="IUsersStore"/> surface.</summary>
public interface IUsersStoreCensus
{
    Task<int> CountProfilesAsync(CancellationToken ct);
}

/// <inheritdoc cref="IUsersStoreCensus"/>
public sealed class UsersStoreCensus(IUsersStore users) : IUsersStoreCensus
{
    public async Task<int> CountProfilesAsync(CancellationToken ct)
    {
        // PageSize 1: Total is the full count and only one row is materialised.
        var page = await users.SearchAsync(new UserSearchQuery { Page = 1, PageSize = 1 }, ct);
        return page.Total;
    }
}
