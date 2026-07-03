using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.Users;

/// <summary>
/// Best-effort proxy of the user-management (UM) profile-get surface, resolved in a
/// short-lived DI scope so a SINGLETON store can safely reach the SCOPED NSwag
/// <see cref="UmClient"/> (registered <c>AddScoped</c> in Program.cs) without a captive
/// dependency. A UM blip — or UM simply not being configured — returns null rather than
/// throwing, so the caller falls back to the durable local projection.
/// </summary>
public interface IUpstreamUserProfileClient
{
    Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct);
}

/// <inheritdoc cref="IUpstreamUserProfileClient"/>
public sealed class ScopedUserManagementProfileClient : IUpstreamUserProfileClient
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScopedUserManagementProfileClient> _log;

    public ScopedUserManagementProfileClient(
        IServiceScopeFactory scopeFactory, ILogger<ScopedUserManagementProfileClient> log)
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
            var um = scope.ServiceProvider.GetRequiredService<UmClient>();
            var resp = await um.ProfileAsync(userId, ct);
            return resp is null ? null : Map(userId, resp);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "user-management ProfileAsync proxy failed for {UserId}; serving local projection", userId);
            return null;
        }
    }

    private static UserProfile Map(string userId, UmProfileResponse r)
    {
        var created = DateTimeOffset.TryParse(r.CreatedDate, out var c) ? c : DateTimeOffset.UtcNow;
        return new UserProfile
        {
            Id = string.IsNullOrWhiteSpace(r.UserId) ? userId : r.UserId!,
            // UM's profile response carries no phone — left blank so the durable
            // projection's blank-preserving upsert keeps any phone already learned.
            Phone = string.Empty,
            Name = r.Username ?? string.Empty,
            Email = r.Email,
            AvatarUrl = r.ProfilePic,
            Roles = r.Available_roles is { Count: > 0 } ? r.Available_roles.ToList() : new List<string>(),
            ActiveRole = string.IsNullOrWhiteSpace(r.Active_role) ? "customer" : r.Active_role!,
            Language = "en",
            CreatedAt = created,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>
/// Durable, upstream-backed <see cref="IUsersStore"/> (JEB users-durable). Registered in
/// place of <see cref="InMemoryUsersStore"/> when <c>GatewayPostgres:ConnectionString</c>
/// is configured, so the gateway's admin user-search and token-mint <c>active_role</c>
/// read no longer evaporate on a bounce.
///
/// <para><b>Composition.</b> Three collaborators, each owning one concern:
/// <list type="bullet">
///   <item><see cref="IUserProjectionStore"/> — the durable Postgres projection of the
///     UM-resolved identity (the <c>users</c> table). Serves admin <see cref="SearchAsync"/>
///     and the durable point-lookup, and receives every identity change best-effort.</item>
///   <item><see cref="InMemoryUsersStore"/> — the permissive in-process behavioural store,
///     kept EXACTLY as-is so all existing behaviour is preserved (a strict superset):
///     saved addresses, permissive non-UUID ids (the OTP UM-down phone-keyed fallback),
///     and the synchronous role/suspension bookkeeping the token mint reads back.</item>
///   <item><see cref="IUpstreamUserProfileClient"/> — a best-effort UM profile-get proxy
///     that hydrates a cold read (the only path that touches UM here; the profile-PATCH
///     surfaces already own the UM <c>UpdateAsync</c> call themselves and use this store
///     purely as their durable local mirror — proxying the PUT again would double-write).</item>
/// </list></para>
///
/// <para><b>Durability contract.</b> Identity mutations write the in-memory store FIRST
/// (authoritative in-process, never fails on a Postgres blip / permissive id) and mirror
/// the durable projection SECOND, best-effort. Reads prefer the durable projection so the
/// data survives a restart; they fall back to the in-memory store and, only when both are
/// cold, to the UM proxy. Identity remains user-management's source of truth — Postgres is
/// a read-model projection, not a second identity authority.</para>
/// </summary>
public sealed class UpstreamBackedUsersStore : IUsersStore
{
    private readonly IUserProjectionStore _projection;
    private readonly InMemoryUsersStore _inner;
    private readonly IUpstreamUserProfileClient _upstream;
    private readonly ILogger<UpstreamBackedUsersStore> _log;

    public UpstreamBackedUsersStore(
        IUserProjectionStore projection,
        InMemoryUsersStore inner,
        IUpstreamUserProfileClient upstream,
        ILogger<UpstreamBackedUsersStore> log)
    {
        _projection = projection;
        _inner = inner;
        _upstream = upstream;
        _log = log;
    }

    public async Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        // 1) Durable projection first (survives a gateway bounce).
        var durable = await SafeProjectionGetAsync(userId, ct);
        if (durable is not null) return durable;

        // 2) In-process store (permissive ids / freshly-seeded rows).
        var local = await _inner.GetByIdAsync(userId, ct);
        if (local is not null) return local;

        // 3) Cold read — best-effort UM profile-get proxy, then persist so the next
        //    lookup is durable and admin search sees the user. Never throws.
        var upstream = await _upstream.GetProfileAsync(userId, ct);
        if (upstream is not null)
        {
            await _inner.UpsertProjectionAsync(upstream, ct);
            await SafeUpsertAsync(upstream, ct);
        }
        return upstream;
    }

    public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
        // Permissive bootstrap (phone-keyed OTP fallback / arbitrary ids) — in-process
        // only. The OTP-verify path immediately follows with UpsertProjectionAsync, which
        // is where a well-formed identity becomes durable.
        => _inner.GetOrCreateAsync(userId, ct);

    public async Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
    {
        // In-memory ALWAYS (authoritative in-process: the token mint reads active_role
        // back from here, and it tolerates the empty-phone / non-UUID projections the
        // super-login and UM-down paths emit). Durable projection best-effort on top.
        await _inner.UpsertProjectionAsync(profile, ct);
        await SafeUpsertAsync(profile, ct);
    }

    public async Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct)
    {
        // The profile-update controllers already performed the UM PUT and call this as
        // the durable local projection mirror; keep the in-memory semantics (authoritative
        // return; null patch fields left untouched) and mirror the change durably.
        var updated = await _inner.UpdateProfileAsync(userId, patch, ct);
        await SafeUpsertAsync(updated, ct);
        return updated;
    }

    // ── Saved addresses — user-private, not part of the identity projection; delegated
    //    verbatim to the in-memory store (durability out of scope for this change). ──

    public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct)
        => _inner.ListAddressesAsync(userId, ct);

    public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct)
        => _inner.GetAddressAsync(userId, addressId, ct);

    public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct)
        => _inner.CreateAddressAsync(userId, input, ct);

    public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct)
        => _inner.UpdateAddressAsync(userId, addressId, patch, ct);

    public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct)
        => _inner.DeleteAddressAsync(userId, addressId, ct);

    public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
        // Durable admin search — served from Postgres so it survives a restart.
        => _projection.SearchAsync(query, ct);

    public async Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct)
    {
        var updated = await _inner.SuspendAsync(userId, reason, adminId, ct);
        if (updated is not null) await SafeSuspensionAsync(updated, ct);
        return updated;
    }

    public async Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct)
    {
        var updated = await _inner.UnsuspendAsync(userId, adminId, ct);
        if (updated is not null) await SafeSuspensionAsync(updated, ct);
        return updated;
    }

    public async Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct)
    {
        var updated = await _inner.SwitchRoleAsync(userId, newRole, ct);
        if (updated is not null) await SafeUpsertAsync(updated, ct);
        return updated;
    }

    public async Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct)
    {
        var updated = await _inner.GrantRoleAsync(userId, role, ct);
        if (updated is not null) await SafeUpsertAsync(updated, ct);
        return updated;
    }

    public async Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
    {
        var removed = await _inner.PurgePiiAsync(userId, ct);
        if (removed) await SafePurgeAsync(userId, ct);
        return removed;
    }

    // ── Best-effort durable mirrors — a Postgres fault never breaks an operation the
    //    in-memory store already completed in-process (strict-superset guarantee). ──

    private async Task<UserProfile?> SafeProjectionGetAsync(string userId, CancellationToken ct)
    {
        try
        {
            return await _projection.GetByIdAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "durable user projection read failed for {UserId}; falling back to in-memory", userId);
            return null;
        }
    }

    private async Task SafeUpsertAsync(UserProfile profile, CancellationToken ct)
    {
        try
        {
            await _projection.UpsertIdentityAsync(profile, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "durable user projection upsert failed for {UserId}; in-memory store stays authoritative in-process",
                profile.Id);
        }
    }

    private async Task SafeSuspensionAsync(UserProfile profile, CancellationToken ct)
    {
        try
        {
            await _projection.SetSuspensionAsync(
                profile.Id, profile.IsSuspended, profile.SuspensionReason, profile.SuspendedAt, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "durable suspension mirror failed for {UserId}", profile.Id);
        }
    }

    private async Task SafePurgeAsync(string userId, CancellationToken ct)
    {
        try
        {
            await _projection.PurgePiiAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "durable PII purge mirror failed for {UserId}", userId);
        }
    }
}
