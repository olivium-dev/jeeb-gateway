using Microsoft.Extensions.DependencyInjection;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.Users;

public interface IUpstreamUserProfileClient
{
    Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Stateless user-management profile reader. A scope is created because the
/// generated owner client is scoped; no response is cached or projected locally.
/// </summary>
public sealed class ScopedUserManagementProfileClient : IUpstreamUserProfileClient
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScopedUserManagementProfileClient> _log;

    public ScopedUserManagementProfileClient(
        IServiceScopeFactory scopeFactory,
        ILogger<ScopedUserManagementProfileClient> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var response = await scope.ServiceProvider.GetRequiredService<UmClient>()
                .ProfileAsync(userId, ct);
            return response is null ? null : Map(userId, response);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "user-management profile read failed for {UserId}", userId);
            return null;
        }
    }

    private static UserProfile Map(string userId, UmProfileResponse response)
    {
        var createdAt = DateTimeOffset.TryParse(response.CreatedDate, out var created)
            ? created
            : DateTimeOffset.UtcNow;
        return new UserProfile
        {
            Id = string.IsNullOrWhiteSpace(response.UserId) ? userId : response.UserId!,
            Phone = string.Empty,
            Name = response.Username ?? string.Empty,
            Email = response.Email,
            AvatarUrl = response.ProfilePic,
            Roles = response.Available_roles?.ToList() ?? new List<string>(),
            ActiveRole = string.IsNullOrWhiteSpace(response.Active_role)
                ? Roles.Client
                : response.Active_role!,
            Language = "en",
            CreatedAt = createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>
/// Stateless compatibility adapter for mobile authentication. User-management
/// remains the sole identity authority. Mutations that have no owner contract fail
/// closed instead of creating a gateway projection.
/// </summary>
public sealed class UpstreamBackedUsersStore : IUsersStore
{
    private readonly IUpstreamUserProfileClient _upstream;

    public UpstreamBackedUsersStore(IUpstreamUserProfileClient upstream) => _upstream = upstream;

    public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct) =>
        _upstream.GetProfileAsync(userId, ct);

    public async Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct) =>
        await _upstream.GetProfileAsync(userId, ct) ?? NewTransientProfile(userId);

    public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct) => Task.CompletedTask;

    public async Task<UserProfile> UpdateProfileAsync(
        string userId,
        ProfilePatch patch,
        CancellationToken ct)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(patch.Name)) profile.Name = patch.Name.Trim();
        if (patch.AvatarUrl is not null) profile.AvatarUrl = patch.AvatarUrl;
        if (!string.IsNullOrWhiteSpace(patch.Language)) profile.Language = patch.Language.Trim();
        if (patch.Email is not null) profile.Email = patch.Email;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        return profile;
    }

    public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SavedAddress>>(Array.Empty<SavedAddress>());

    public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct) =>
        Task.FromResult<SavedAddress?>(null);

    public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct) =>
        Unsupported<SavedAddress>();

    public Task<SavedAddress?> UpdateAddressAsync(
        string userId,
        string addressId,
        AddressUpsert patch,
        CancellationToken ct) => Unsupported<SavedAddress?>();

    public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct) =>
        Unsupported<bool>();

    public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct) =>
        Unsupported<UserSearchResult>();

    public Task<UserProfile?> SuspendAsync(
        string userId,
        string reason,
        string adminId,
        CancellationToken ct) => Unsupported<UserProfile?>();

    public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct) =>
        Unsupported<UserProfile?>();

    public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct) =>
        _upstream.GetProfileAsync(userId, ct);

    public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct) =>
        _upstream.GetProfileAsync(userId, ct);

    public Task<bool> PurgePiiAsync(string userId, CancellationToken ct) => Unsupported<bool>();

    private static UserProfile NewTransientProfile(string userId) => new()
    {
        Id = userId,
        Phone = string.Empty,
        Name = string.Empty,
        Roles = new List<string> { Roles.Client },
        ActiveRole = Roles.Client,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<T> Unsupported<T>() => Task.FromException<T>(
        new NotSupportedException("This operation has no authoritative owner contract and is disabled in the stateless gateway."));
}
