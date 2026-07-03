using System.Collections.Concurrent;

namespace JeebGateway.Users;

/// <summary>
/// In-memory implementation used by integration tests and local runs
/// before the gateway is wired to the auth-service. Mirrors the
/// invariants enforced by db/migrations/0006:
///   * label uniqueness per user (case-insensitive)
///   * at most one default address per user
/// </summary>
public class InMemoryUsersStore : IUsersStore
{
    private readonly ConcurrentDictionary<string, UserProfile> _users = new();
    private readonly ConcurrentDictionary<string, SavedAddress> _addresses = new();
    private readonly object _writeLock = new();

    public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        _users.TryGetValue(userId, out var profile);
        return Task.FromResult(profile is null ? null : Clone(profile));
    }

    public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
    {
        var profile = _users.GetOrAdd(userId, id => NewDefault(id));
        return Task.FromResult(Clone(profile));
    }

    /// <summary>
    /// S02 Wave-1 (ADR-003, F-C). Idempotent upsert of an upstream-resolved identity
    /// projection. Replaces the id row so the gateway-minted JWT embeds the OPAQUE
    /// roles/active_role user-management persisted. Preserves any existing saved
    /// addresses on the row by leaving the address store untouched.
    ///
    /// <para>feat/tier-unify-names (jeeberName gap): the OTP-verify / super-login
    /// callers project IDENTITY fields only (id/phone/roles/active role) and carry
    /// Name = "" — so a blind row replace WIPED any locally-known display fields on
    /// every re-login, which is why the deliveries jeeberName enrichment emitted
    /// nothing on real accounts. Locally-known DISPLAY fields (Name / Email /
    /// AvatarUrl) now survive a projection upsert whose incoming values are blank;
    /// an incoming NON-blank display value still wins (the upstream stays
    /// authoritative when it actually supplies one).</para>
    /// </summary>
    public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var incoming = Clone(profile);
            if (_users.TryGetValue(profile.Id, out var existing))
            {
                if (string.IsNullOrWhiteSpace(incoming.Name) && !string.IsNullOrWhiteSpace(existing.Name))
                    incoming.Name = existing.Name;
                if (string.IsNullOrWhiteSpace(incoming.Email) && !string.IsNullOrWhiteSpace(existing.Email))
                    incoming.Email = existing.Email;
                if (string.IsNullOrWhiteSpace(incoming.AvatarUrl) && !string.IsNullOrWhiteSpace(existing.AvatarUrl))
                    incoming.AvatarUrl = existing.AvatarUrl;
            }

            _users[profile.Id] = incoming;
        }

        return Task.CompletedTask;
    }

    public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var existing = _users.GetOrAdd(userId, NewDefault);
            if (patch.Name is { } name) existing.Name = name;
            if (patch.AvatarUrl is not null) existing.AvatarUrl = patch.AvatarUrl;
            if (patch.Language is { } lang) existing.Language = lang;
            if (patch.Email is not null) existing.Email = patch.Email;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(Clone(existing));
        }
    }

    public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct)
    {
        var list = _addresses.Values
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.CreatedAt)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<SavedAddress>>(list);
    }

    public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct)
    {
        _addresses.TryGetValue(addressId, out var addr);
        if (addr is null || addr.UserId != userId) return Task.FromResult<SavedAddress?>(null);
        return Task.FromResult<SavedAddress?>(Clone(addr));
    }

    public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct)
    {
        lock (_writeLock)
        {
            _users.GetOrAdd(userId, NewDefault);

            var label = input.Label ?? throw new ArgumentException("Label is required.", nameof(input));
            var line1 = input.Line1 ?? throw new ArgumentException("Line1 is required.", nameof(input));

            if (_addresses.Values.Any(a => a.UserId == userId
                && string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicateAddressLabelException(label);
            }

            var now = DateTimeOffset.UtcNow;
            var addr = new SavedAddress
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Label = label,
                Line1 = line1,
                Line2 = input.Line2,
                City = input.City,
                Country = input.Country,
                Latitude = input.Latitude,
                Longitude = input.Longitude,
                IsDefault = input.IsDefault ?? false,
                CreatedAt = now,
                UpdatedAt = now
            };

            if (addr.IsDefault) ClearOtherDefaults(userId, addr.Id);

            _addresses[addr.Id] = addr;
            return Task.FromResult(Clone(addr));
        }
    }

    public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_addresses.TryGetValue(addressId, out var addr) || addr.UserId != userId)
            {
                return Task.FromResult<SavedAddress?>(null);
            }

            if (patch.Label is { } newLabel && !string.Equals(newLabel, addr.Label, StringComparison.OrdinalIgnoreCase))
            {
                if (_addresses.Values.Any(a => a.UserId == userId
                    && a.Id != addressId
                    && string.Equals(a.Label, newLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DuplicateAddressLabelException(newLabel);
                }
                addr.Label = newLabel;
            }
            if (patch.Line1 is { } l1) addr.Line1 = l1;
            if (patch.Line2 is not null) addr.Line2 = patch.Line2;
            if (patch.City is not null) addr.City = patch.City;
            if (patch.Country is not null) addr.Country = patch.Country;
            if (patch.Latitude.HasValue) addr.Latitude = patch.Latitude;
            if (patch.Longitude.HasValue) addr.Longitude = patch.Longitude;
            if (patch.IsDefault is { } isDefault)
            {
                addr.IsDefault = isDefault;
                if (isDefault) ClearOtherDefaults(userId, addressId);
            }
            addr.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<SavedAddress?>(Clone(addr));
        }
    }

    public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_addresses.TryGetValue(addressId, out var addr) || addr.UserId != userId)
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(_addresses.TryRemove(addressId, out _));
        }
    }

    public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserProfile?>(null);
            }

            existing.IsSuspended = true;
            existing.SuspensionReason = reason;
            existing.SuspendedAt = DateTimeOffset.UtcNow;
            existing.SuspendedBy = adminId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<UserProfile?>(Clone(existing));
        }
    }

    public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserProfile?>(null);
            }

            existing.ActiveRole = newRole;
            existing.RoleSwitchedAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<UserProfile?>(Clone(existing));
        }
    }

    public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserProfile?>(null);
            }

            if (!existing.Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)))
            {
                existing.Roles.Add(role);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return Task.FromResult<UserProfile?>(Clone(existing));
        }
    }

    public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserProfile?>(null);
            }

            existing.IsSuspended = false;
            existing.SuspensionReason = null;
            existing.SuspendedAt = null;
            existing.SuspendedBy = null;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<UserProfile?>(Clone(existing));
        }
    }

    public Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult(false);
            }

            existing.Name = string.Empty;
            existing.Email = null;
            existing.AvatarUrl = null;
            // Phone is `init`-only on UserProfile — replace the row so PII
            // is genuinely gone, not just hidden behind a stale reference.
            var purged = new UserProfile
            {
                Id = existing.Id,
                Phone = string.Empty,
                Email = null,
                Name = string.Empty,
                AvatarUrl = null,
                Language = existing.Language,
                Roles = existing.Roles,
                Rating = existing.Rating,
                RatingCount = existing.RatingCount,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsSuspended = existing.IsSuspended,
                SuspensionReason = existing.SuspensionReason,
                SuspendedAt = existing.SuspendedAt,
                SuspendedBy = existing.SuspendedBy,
                ActiveRole = existing.ActiveRole,
                RoleSwitchedAt = existing.RoleSwitchedAt
            };
            _users[userId] = purged;

            foreach (var addrId in _addresses
                .Where(kv => kv.Value.UserId == userId)
                .Select(kv => kv.Key)
                .ToList())
            {
                _addresses.TryRemove(addrId, out _);
            }

            return Task.FromResult(true);
        }
    }

    public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
    {
        IEnumerable<UserProfile> matches = _users.Values;

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var needle = query.Name.Trim();
            matches = matches.Where(u => u.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.Phone))
        {
            var needle = query.Phone.Trim();
            matches = matches.Where(u => u.Phone.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var needle = query.Email.Trim();
            matches = matches.Where(u =>
                !string.IsNullOrEmpty(u.Email)
                && u.Email.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = matches.OrderByDescending(u => u.CreatedAt).ToList();
        var total = ordered.Count;
        var page = Math.Max(query.Page, 1);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var slice = ordered.Skip((page - 1) * size).Take(size).Select(Clone).ToList();

        return Task.FromResult(new UserSearchResult
        {
            Items = slice,
            Total = total
        });
    }

    private void ClearOtherDefaults(string userId, string keepId)
    {
        foreach (var a in _addresses.Values.Where(a => a.UserId == userId && a.Id != keepId && a.IsDefault))
        {
            a.IsDefault = false;
            a.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static UserProfile NewDefault(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        return new UserProfile
        {
            Id = userId,
            Phone = string.Empty,
            Name = string.Empty,
            Language = "en",
            Roles = new List<string> { "customer" },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static UserProfile Clone(UserProfile p) => new()
    {
        Id = p.Id,
        Phone = p.Phone,
        Email = p.Email,
        Name = p.Name,
        AvatarUrl = p.AvatarUrl,
        Language = p.Language,
        Roles = new List<string>(p.Roles),
        Rating = p.Rating,
        RatingCount = p.RatingCount,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        IsSuspended = p.IsSuspended,
        SuspensionReason = p.SuspensionReason,
        SuspendedAt = p.SuspendedAt,
        SuspendedBy = p.SuspendedBy,
        ActiveRole = p.ActiveRole,
        RoleSwitchedAt = p.RoleSwitchedAt
    };

    private static SavedAddress Clone(SavedAddress a) => new()
    {
        Id = a.Id,
        UserId = a.UserId,
        Label = a.Label,
        Line1 = a.Line1,
        Line2 = a.Line2,
        City = a.City,
        Country = a.Country,
        Latitude = a.Latitude,
        Longitude = a.Longitude,
        IsDefault = a.IsDefault,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt
    };

    /// <summary>
    /// Test/seed helper — sets identity fields the in-memory bootstrap
    /// can't infer from a bare user-id (phone, name, roles, rating).
    /// Not part of <see cref="IUsersStore"/>; production storage will
    /// own user creation through the auth-service.
    /// </summary>
    public void Seed(UserProfile profile)
    {
        _users[profile.Id] = Clone(profile);
    }
}
