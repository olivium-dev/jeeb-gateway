using JeebGateway.Services.Clients;
using JeebGateway.Users.SavedLocations;
using Microsoft.Extensions.DependencyInjection;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;
using UmUpdateRequest = JeebGateway.service.ServiceUserManagement.UpdateUserProfileRequest;

namespace JeebGateway.Users;

/// <summary>
/// Stateless compatibility implementation of the legacy <see cref="IUsersStore"/>
/// surface. Every live operation is routed to the owning service: identity/roles
/// to user-management, suspension to ban-service, ratings through the composed
/// admin projection, and saved addresses to remote-user-preferences. No result is
/// retained in gateway memory or Postgres.
/// </summary>
public sealed class OwnerBackedUsersStore : IUsersStore
{
    private readonly IServiceScopeFactory _scopes;

    public OwnerBackedUsersStore(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        await using var scope = _scopes.CreateAsyncScope();
        try
        {
            var identity = await scope.ServiceProvider.GetRequiredService<UmClient>()
                .ProfileAsync(userId, ct);
            return MapIdentity(identity);
        }
        catch (UmApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// R5: moderation-authoritative read. Deliberately NOT the interface default — that
    /// delegates to <see cref="GetByIdAsync"/>, which resolves identity from
    /// user-management, and that contract carries no suspension field. A suspended user
    /// would therefore read back as active and be minted a session. Suspension is owned
    /// by ban-service, so it is read from there and any fault PROPAGATES: the caller
    /// (<see cref="UserModerationGate"/>) turns it into Unavailable, never into a pass.
    /// </summary>
    public async Task<UserProfile?> GetForModerationAsync(string userId, CancellationToken ct)
    {
        var profile = await GetByIdAsync(userId, ct);
        if (profile is null)
        {
            // A genuinely absent identity still proceeds, matching UserModerationGate.
            return null;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var statuses = await scope.ServiceProvider.GetRequiredService<IBanServiceClient>()
            .GetStatusAsync(userId, ct);

        var active = statuses.BanStatuses
            .Where(status => status.IsCurrentlyBanned)
            .OrderByDescending(status => status.LastUpdated)
            .FirstOrDefault();
        if (active is not null)
        {
            profile.IsSuspended = true;
            profile.SuspensionReason = active.Message;
        }

        return profile;
    }

    public async Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
    {
        var existing = await GetByIdAsync(userId, ct);
        if (existing is not null)
        {
            return existing;
        }

        if (userId.StartsWith('+'))
        {
            await using var scope = _scopes.CreateAsyncScope();
            var owner = scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>();
            var created = await owner.PhoneFindOrCreateAsync(userId, ct);
            return new UserProfile
            {
                Id = created.UserId,
                Phone = userId,
                Name = string.Empty,
                Roles = created.AvailableRoles.ToList(),
                ActiveRole = created.ActiveRole,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        throw new KeyNotFoundException(
            $"User '{userId}' does not exist in user-management.");
    }

    public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
        => throw new NotSupportedException(
            "Gateway user projections are retired; write the owning service instead.");

    public async Task<UserProfile> UpdateProfileAsync(
        string userId, ProfilePatch patch, CancellationToken ct)
    {
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var owner = scope.ServiceProvider.GetRequiredService<UmClient>();
            await owner.UpdateAsync(new UmUpdateRequest
            {
                UserId = userId,
                Username = patch.Name,
                ProfilePic = patch.AvatarUrl,
                Email = patch.Email,
            }, ct);
        }

        return await GetByIdAsync(userId, ct)
               ?? throw new KeyNotFoundException(
                   $"User '{userId}' disappeared after user-management accepted the update.");
    }

    public async Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(
        string userId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ISavedLocationStore>();
        return (await owner.ListAsync(userId, ct)).Select(ToAddress).ToList();
    }

    public async Task<SavedAddress?> GetAddressAsync(
        string userId, string addressId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ISavedLocationStore>();
        var location = await owner.GetAsync(userId, addressId, ct);
        return location is null ? null : ToAddress(location);
    }

    public async Task<SavedAddress> CreateAddressAsync(
        string userId, AddressUpsert input, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ISavedLocationStore>();
        var location = await owner.CreateAsync(userId, new CreateSavedLocationRequest
        {
            Label = input.Label ?? throw new ArgumentException("Label is required.", nameof(input)),
            Address = JoinAddress(input),
            Latitude = Convert.ToDouble(input.Latitude ?? 0),
            Longitude = Convert.ToDouble(input.Longitude ?? 0),
            IsDefault = input.IsDefault ?? false,
        }, ct);
        return ToAddress(location);
    }

    public async Task<SavedAddress?> UpdateAddressAsync(
        string userId, string addressId, AddressUpsert patch, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ISavedLocationStore>();
        var location = await owner.UpdateAsync(userId, addressId, new UpdateSavedLocationRequest
        {
            Label = patch.Label,
            Address = JoinAddress(patch),
            Latitude = patch.Latitude is null ? null : Convert.ToDouble(patch.Latitude.Value),
            Longitude = patch.Longitude is null ? null : Convert.ToDouble(patch.Longitude.Value),
            IsDefault = patch.IsDefault,
        }, ct);
        return location is null ? null : ToAddress(location);
    }

    public async Task<bool> DeleteAddressAsync(
        string userId, string addressId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISavedLocationStore>()
            .DeleteAsync(userId, addressId, ct);
    }

    public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
        => WithProjectionAsync(projection => projection.SearchAsync(query, ct));

    public Task<UserProfile?> SuspendAsync(
        string userId, string reason, string adminId, CancellationToken ct)
        => WithProjectionAsync(projection =>
            projection.SuspendAsync(userId, reason, adminId, ct));

    public Task<UserProfile?> UnsuspendAsync(
        string userId, string adminId, CancellationToken ct)
        => WithProjectionAsync(projection => projection.UnsuspendAsync(userId, adminId, ct));

    public async Task<UserProfile?> SwitchRoleAsync(
        string userId, string newRole, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>()
            .RoleSwitchAsync(userId, newRole, ct);
        return await GetByIdAsync(userId, ct);
    }

    public async Task<UserProfile?> GrantRoleAsync(
        string userId, string role, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>()
            .AppendAvailableRoleAsync(userId, role, ct);
        var profile = await GetByIdAsync(userId, ct);
        if (profile is not null)
        {
            profile.Roles = result.AvailableRoles.ToList();
        }
        return profile;
    }

    public async Task<UserProfile?> RevokeRoleAsync(
        string userId, string role, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>()
            .RemoveAvailableRoleAsync(userId, role, ct);
        var profile = await GetByIdAsync(userId, ct);
        if (profile is not null)
        {
            profile.Roles = result.AvailableRoles.ToList();
            if (!profile.Roles.Contains(profile.ActiveRole, StringComparer.OrdinalIgnoreCase))
            {
                profile.ActiveRole = profile.Roles.FirstOrDefault() ?? Roles.Client;
            }
        }
        return profile;
    }

    public async Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UmClient>();
        var identityPurged = true;
        try
        {
            var result = await users.DeleteAsync(userId, ct);
            identityPurged = result.Success;
        }
        catch (UmApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            // A durable deletion can be replayed after user-management committed
            // the first delete but before state-service recorded completion. The
            // canonical identity is already absent, so continue the remaining
            // idempotent owner cleanup instead of exhausting the work item.
        }

        var savedLocations = scope.ServiceProvider.GetRequiredService<ISavedLocationStore>();
        foreach (var location in await savedLocations.ListAsync(userId, ct))
        {
            await savedLocations.DeleteAsync(userId, location.Id, ct);
        }

        await scope.ServiceProvider.GetRequiredService<IBanServiceClient>()
            .ForceResetAsync(userId, ct);
        return identityPurged;
    }

    private async Task<T> WithProjectionAsync<T>(
        Func<IAdminUserProjection, Task<T>> operation)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IAdminUserProjection>());
    }

    private static SavedAddress ToAddress(SavedLocation location) => new()
    {
        Id = location.Id,
        UserId = location.UserId,
        Label = location.Label,
        Line1 = location.Address ?? string.Empty,
        Latitude = Convert.ToDecimal(location.Latitude),
        Longitude = Convert.ToDecimal(location.Longitude),
        IsDefault = location.IsDefault,
        CreatedAt = location.CreatedAt,
        UpdatedAt = location.UpdatedAt,
    };

    private static UserProfile MapIdentity(UmProfile identity)
    {
        var created = DateTimeOffset.TryParse(identity.CreatedDate, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
        var roles = identity.Available_roles?
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToList() ?? new List<string>();
        var activeRole = identity.Active_role;
        if (string.IsNullOrWhiteSpace(activeRole)
            || !roles.Contains(activeRole, StringComparer.OrdinalIgnoreCase))
        {
            activeRole = roles.FirstOrDefault() ?? Roles.Client;
        }

        return new UserProfile
        {
            Id = identity.UserId ?? string.Empty,
            Phone = string.Empty,
            Email = identity.Email,
            Name = identity.Username ?? string.Empty,
            AvatarUrl = identity.ProfilePic,
            Language = "en",
            Roles = roles,
            ActiveRole = activeRole,
            CreatedAt = created,
            UpdatedAt = created,
        };
    }

    private static string? JoinAddress(AddressUpsert input)
    {
        var pieces = new[] { input.Line1, input.Line2, input.City, input.Country }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        return pieces.Length == 0 ? null : string.Join(", ", pieces);
    }
}
