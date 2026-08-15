using System.Collections.Concurrent;
using JeebGateway.ProhibitedItems;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>
/// Explicit test-only owner doubles. Production never registers these types;
/// individual integration-test hosts opt in when their subject is unrelated to
/// user-management or ban-service transport behavior.
/// </summary>
internal static class OwnerServiceFakes
{
    public static void UseInMemoryUsers(IServiceCollection services)
    {
        services.RemoveAll<IUsersStore>();
        services.RemoveAll<InMemoryUsersStore>();
        services.AddSingleton<InMemoryUsersStore>();
        services.AddSingleton<IUsersStore>(sp =>
            sp.GetRequiredService<InMemoryUsersStore>());
    }

    public static void AllowAllAccounts(IServiceCollection services)
    {
        services.RemoveAll<IBanServiceClient>();
        services.AddSingleton<IBanServiceClient, AllowAllBanServiceClient>();
    }

    /// <summary>
    /// Explicit endpoint-test fixture for tests whose subject is gateway
    /// moderation behavior rather than the ban-service transport seam. Runtime
    /// never registers this local owner double.
    /// </summary>
    public static void UseSeededModerationCatalog(IServiceCollection services)
    {
        var store = ReplaceModerationOwner(services);
        Seed(store, "arak", "alcohol", ProhibitedSeverity.Block);
        Seed(store, "alcohol", "alcohol", ProhibitedSeverity.Block);
        Seed(store, "kitchen knife", "weapon", ProhibitedSeverity.Warn);
        Seed(store, "knife", "weapon", ProhibitedSeverity.Warn);
    }

    public static void UseEmptyModerationCatalog(IServiceCollection services)
        => ReplaceModerationOwner(services);

    private static InMemoryProhibitedItemsStore ReplaceModerationOwner(
        IServiceCollection services)
    {
        services.RemoveAll<IProhibitedItemsStore>();
        var store = new InMemoryProhibitedItemsStore();
        services.AddSingleton<IProhibitedItemsStore>(store);
        return store;
    }

    private static void Seed(
        InMemoryProhibitedItemsStore store,
        string name,
        string category,
        ProhibitedSeverity severity)
    {
        store.CreateAsync(new ProhibitedItemCreate
        {
            Name = name,
            Category = category,
            Severity = severity,
        }, "test-owner-fixture", CancellationToken.None).GetAwaiter().GetResult();
    }
}

internal sealed class AllowAllBanServiceClient : IBanServiceClient
{
    public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
        => Task.FromResult(new BanStatusesResult
        {
            UserId = userId,
            BanStatuses = Array.Empty<BanStatusItem>(),
        });

    public Task<BanStatusItem> ApplyBanAsync(
        string userId, string banType, CancellationToken ct)
        => Task.FromResult(Status(userId, banType));

    public Task<BanStatusItem> ApplyTerminalBanAsync(
        string userId, string policyKey, CancellationToken ct)
        => Task.FromResult(Status(userId, policyKey));

    public Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
        => Task.FromResult(new BanResetResult
        {
            Updated = false,
        });

    private static BanStatusItem Status(string userId, string policyKey) => new()
    {
        UserId = userId,
        BanType = policyKey,
        Status = "BAN",
        IsCurrentlyBanned = true,
        LastUpdated = DateTimeOffset.UtcNow,
    };
}

/// <summary>
/// Small stateful user-management owner used by endpoint tests that need a
/// canonical phone identity and role read without a live service.
/// </summary>
internal sealed class TestUserManagementDualRoleClient : IUserManagementDualRoleClient
{
    private readonly ConcurrentDictionary<string, string> _usersByPhone =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserRolesResult> _rolesByUser =
        new(StringComparer.Ordinal);

    public void Seed(
        string userId,
        IReadOnlyList<string>? roles = null,
        string? activeRole = null)
    {
        var available = roles is { Count: > 0 }
            ? roles.ToArray()
            : new[] { Roles.Client };
        _rolesByUser[userId] = new UserRolesResult(
            userId,
            available,
            activeRole ?? available[0]);
    }

    public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(
        string phone, CancellationToken ct)
    {
        var isNew = false;
        var userId = _usersByPhone.GetOrAdd(phone, _ =>
        {
            isNew = true;
            return $"test-user-{Guid.NewGuid():N}";
        });
        var roles = _rolesByUser.GetOrAdd(
            userId,
            id => new UserRolesResult(id, new[] { Roles.Client }, Roles.Client));
        return Task.FromResult(new PhoneFindOrCreateResult(
            userId, isNew, roles.AvailableRoles, roles.ActiveRole ?? Roles.Client));
    }

    public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
    {
        var roles = _rolesByUser.GetOrAdd(
            userId,
            id => new UserRolesResult(id, new[] { Roles.Client }, Roles.Client));
        return Task.FromResult<UserRolesResult?>(roles);
    }

    public Task<RoleSwitchReissueResult> RoleSwitchAsync(
        string userId, string opaqueRole, CancellationToken ct)
    {
        var current = _rolesByUser.GetOrAdd(
            userId,
            id => new UserRolesResult(id, new[] { Roles.Client }, Roles.Client));
        if (!current.AvailableRoles.Contains(opaqueRole, StringComparer.OrdinalIgnoreCase))
        {
            throw new UserManagementRoleNotAvailableException(userId, opaqueRole);
        }

        _rolesByUser[userId] = current with { ActiveRole = opaqueRole };
        return Task.FromResult(new RoleSwitchReissueResult(
            userId, "test-access", "test-refresh", opaqueRole));
    }

    public Task<RoleGrantResult> AppendAvailableRoleAsync(
        string userId, string opaqueRole, CancellationToken ct)
    {
        var current = _rolesByUser.GetOrAdd(
            userId,
            id => new UserRolesResult(id, new[] { Roles.Client }, Roles.Client));
        var added = !current.AvailableRoles.Contains(
            opaqueRole, StringComparer.OrdinalIgnoreCase);
        var roles = added
            ? current.AvailableRoles.Append(opaqueRole).ToArray()
            : current.AvailableRoles;
        _rolesByUser[userId] = current with { AvailableRoles = roles };
        return Task.FromResult(new RoleGrantResult(userId, roles, added));
    }

    public Task<RoleGrantResult> RemoveAvailableRoleAsync(
        string userId, string opaqueRole, CancellationToken ct)
    {
        var current = _rolesByUser.GetOrAdd(
            userId,
            id => new UserRolesResult(id, new[] { Roles.Client }, Roles.Client));
        var roles = current.AvailableRoles
            .Where(role => !string.Equals(
                role, opaqueRole, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var active = roles.Contains(current.ActiveRole, StringComparer.OrdinalIgnoreCase)
            ? current.ActiveRole
            : roles.FirstOrDefault();
        _rolesByUser[userId] = current with
        {
            AvailableRoles = roles,
            ActiveRole = active,
        };
        return Task.FromResult(new RoleGrantResult(
            userId, roles, roles.Length != current.AvailableRoles.Count));
    }
}
