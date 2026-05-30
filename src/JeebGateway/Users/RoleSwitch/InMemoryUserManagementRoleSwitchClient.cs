using System.Collections.Concurrent;

namespace JeebGateway.Users.RoleSwitch;

/// <summary>
/// MVP / test in-memory backing for <see cref="IUserManagementRoleSwitchClient"/>.
/// Tracks per-user dual-role identity (<c>available_roles</c>, <c>active_role</c>,
/// <c>active_role_changed_at</c>) using the same shape as the user-management
/// T-BE-002 storage. Tests seed users via <see cref="Seed"/>; the controller
/// reads and mutates through the interface.
///
/// Default state for new (unseen) users: returned as <see cref="RoleSwitchOutcome.UserNotFound"/>
/// — the controller maps that to a 404 ProblemDetails, exactly as the
/// production HTTP adapter does when user-management returns 404. Seeded
/// users follow the user-management default of
/// <c>available_roles=['client']</c>, <c>active_role='client'</c> from the
/// T-BE-002 migration unless explicitly overridden by the test.
///
/// Production wiring replaces this registration with an HTTP adapter over
/// the NSwag-generated <c>UserManagementClient</c>.
/// </summary>
public sealed class InMemoryUserManagementRoleSwitchClient
    : IUserManagementRoleSwitchClient
{
    public const string RoleClient = "client";
    public const string RoleJeeber = "jeeber";

    private readonly ConcurrentDictionary<Guid, Record> _users = new();
    private readonly object _writeLock = new();

    public Task<RoleSwitchUserSnapshot?> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (_users.TryGetValue(userId, out var record))
        {
            return Task.FromResult<RoleSwitchUserSnapshot?>(record.ToSnapshot());
        }
        return Task.FromResult<RoleSwitchUserSnapshot?>(null);
    }

    public Task<RoleSwitchResult> SwitchActiveRoleAsync(
        Guid userId, string newRole, CancellationToken ct = default)
    {
        lock (_writeLock)
        {
            if (!_users.TryGetValue(userId, out var record))
            {
                return Task.FromResult(new RoleSwitchResult(
                    RoleSwitchOutcome.UserNotFound, null, null));
            }

            if (!record.AvailableRoles.Contains(newRole, StringComparer.Ordinal))
            {
                return Task.FromResult(new RoleSwitchResult(
                    RoleSwitchOutcome.RoleNotAvailable, record.ToSnapshot(), record.ActiveRole));
            }

            var previousRole = record.ActiveRole;
            // Same-role switches are idempotent but still update the
            // change timestamp so audit logs can see the heartbeat.
            record.ActiveRole = newRole;
            record.ActiveRoleChangedAt = DateTimeOffset.UtcNow;

            return Task.FromResult(new RoleSwitchResult(
                RoleSwitchOutcome.Ok, record.ToSnapshot(), previousRole));
        }
    }

    /// <summary>
    /// Test/seed helper — not part of the public interface. Production
    /// adapters do not expose seed methods; they delegate to user-management.
    /// </summary>
    public void Seed(Guid userId, IEnumerable<string> availableRoles, string activeRole)
    {
        var rolesList = availableRoles?.ToList()
            ?? throw new ArgumentNullException(nameof(availableRoles));
        if (rolesList.Count == 0)
        {
            throw new ArgumentException(
                "available_roles must not be empty.", nameof(availableRoles));
        }
        if (!rolesList.Contains(activeRole, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"active_role '{activeRole}' must be present in available_roles " +
                $"[{string.Join(", ", rolesList)}].", nameof(activeRole));
        }

        _users[userId] = new Record
        {
            UserId = userId,
            AvailableRoles = rolesList,
            ActiveRole = activeRole,
            ActiveRoleChangedAt = null
        };
    }

    /// <summary>Removes a seeded user (test cleanup).</summary>
    public bool Remove(Guid userId) => _users.TryRemove(userId, out _);

    private sealed class Record
    {
        public required Guid UserId { get; init; }
        public required List<string> AvailableRoles { get; init; }
        public required string ActiveRole { get; set; }
        public DateTimeOffset? ActiveRoleChangedAt { get; set; }

        public RoleSwitchUserSnapshot ToSnapshot() => new(
            UserId,
            AvailableRoles.ToList(),
            ActiveRole,
            ActiveRoleChangedAt);
    }
}
