using FluentAssertions;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Regression coverage for the Role Service authority cutover. Jeeber is an additive role:
/// every user permanently retains customer, while active_role selects the current persona.
/// </summary>
public sealed class RoleServiceBackedDualRoleClientTests
{
    [Fact]
    public async Task GetRoles_WhenOnlyJeeberWasBackfilled_RepairsAndReturnsBothRoles()
    {
        var inner = new StubInner();
        var roles = new InMemoryRoleService(new[] { Roles.Jeeber }, Roles.Jeeber);
        var sut = Create(inner, roles, enabled: true);

        var result = await sut.GetUserRolesAsync("u-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.AvailableRoles.Should().BeEquivalentTo(Roles.Client, Roles.Jeeber);
        result.ActiveRole.Should().Be(Roles.Jeeber);
        roles.GrantedRoles.Should().Equal(Roles.Client);
        inner.GetRolesCalls.Should().Be(0);
    }

    [Fact]
    public async Task AppendJeeber_GrantsPermanentClientFirst_AndReturnsAdditiveSet()
    {
        var inner = new StubInner();
        var roles = new InMemoryRoleService();
        var sut = Create(inner, roles, enabled: true);

        var result = await sut.AppendAvailableRoleAsync("u-1", Roles.Jeeber, CancellationToken.None);

        roles.GrantedRoles.Should().Equal(Roles.Client, Roles.Jeeber);
        result.AvailableRoles.Should().BeEquivalentTo(Roles.Client, Roles.Jeeber);
        result.Added.Should().BeTrue();
        inner.AppendCalls.Should().Be(0);
    }

    [Fact]
    public async Task SwitchRole_UsesRoleServiceAuthority_AndKeepsBothMemberships()
    {
        var inner = new StubInner();
        var roles = new InMemoryRoleService(new[] { Roles.Client, Roles.Jeeber }, Roles.Client);
        var sut = Create(inner, roles, enabled: true);

        var result = await sut.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        roles.SetActiveRoles.Should().Equal(Roles.Jeeber);
        roles.CurrentRoles.Should().BeEquivalentTo(Roles.Client, Roles.Jeeber);
        result.ActiveRole.Should().Be(Roles.Jeeber);
        result.AccessToken.Should().BeEmpty("the controller mints the gateway-audience token");
        result.RefreshToken.Should().BeEmpty();
        inner.RoleSwitchCalls.Should().Be(0);
    }

    [Fact]
    public async Task SwitchRole_WhenJeeberIsNotGranted_PreservesRoleNotAvailableContract()
    {
        var inner = new StubInner();
        var roles = new InMemoryRoleService(new[] { Roles.Client }, Roles.Client);
        var sut = Create(inner, roles, enabled: true);

        var act = () => sut.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        await act.Should().ThrowAsync<UserManagementRoleNotAvailableException>();
        roles.SetActiveRoles.Should().BeEmpty();
        inner.RoleSwitchCalls.Should().Be(0);
    }

    [Fact]
    public async Task RemoveJeeber_FromPartialRecord_LeavesPermanentClientRoleActive()
    {
        var inner = new StubInner();
        var roles = new InMemoryRoleService(new[] { Roles.Jeeber }, Roles.Jeeber);
        var sut = Create(inner, roles, enabled: true);

        var result = await sut.RemoveAvailableRoleAsync("u-1", Roles.Jeeber, CancellationToken.None);

        result.AvailableRoles.Should().Equal(Roles.Client);
        roles.CurrentRoles.Should().Equal(Roles.Client);
        roles.ActiveRole.Should().Be(Roles.Client);
        roles.LastReassignTarget.Should().Be(Roles.Client);
    }

    [Fact]
    public async Task FlagOff_StillDelegatesSwitchToUserManagement()
    {
        var inner = new StubInner
        {
            RoleSwitchResult = new RoleSwitchReissueResult(
                "u-1", "um-access", "um-refresh", Roles.Jeeber),
        };
        var roles = new InMemoryRoleService();
        var sut = Create(inner, roles, enabled: false);

        var result = await sut.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        result.AccessToken.Should().Be("um-access");
        inner.RoleSwitchCalls.Should().Be(1);
        roles.SetActiveRoles.Should().BeEmpty();
    }

    private static RoleServiceBackedDualRoleClient Create(
        StubInner inner, InMemoryRoleService roles, bool enabled) =>
        new(
            inner,
            roles,
            new StaticOptionsMonitor<UpstreamFeatureFlags>(
                new UpstreamFeatureFlags { RoleService = enabled }),
            NullLogger<RoleServiceBackedDualRoleClient>.Instance);

    private sealed class StubInner : IUserManagementDualRoleClient
    {
        public int AppendCalls { get; private set; }
        public int GetRolesCalls { get; private set; }
        public int RoleSwitchCalls { get; private set; }

        public RoleSwitchReissueResult RoleSwitchResult { get; init; } =
            new("u-1", "access", "refresh", Roles.Client);

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct) =>
            Task.FromResult(new PhoneFindOrCreateResult("u-1", false, new[] { Roles.Client }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(
            string userId, string opaqueRole, CancellationToken ct)
        {
            RoleSwitchCalls++;
            return Task.FromResult(RoleSwitchResult);
        }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(
            string userId, string opaqueRole, CancellationToken ct)
        {
            AppendCalls++;
            return Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, true));
        }

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(
            string userId, string opaqueRole, CancellationToken ct) =>
            Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client }, true));

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
        {
            GetRolesCalls++;
            return Task.FromResult<UserRolesResult?>(
                new UserRolesResult(userId, new[] { Roles.Client }, Roles.Client));
        }
    }

    private sealed class InMemoryRoleService : IRoleServiceClient
    {
        private readonly List<string> _roles;

        public InMemoryRoleService(IEnumerable<string>? roles = null, string? activeRole = null)
        {
            _roles = roles?.ToList() ?? new List<string>();
            ActiveRole = activeRole;
        }

        public List<string> GrantedRoles { get; } = new();
        public List<string> SetActiveRoles { get; } = new();
        public IReadOnlyList<string> CurrentRoles => _roles;
        public string? ActiveRole { get; private set; }
        public string? LastReassignTarget { get; private set; }

        public Task<RoleServiceSubjectRoles> GetOrCreateAsync(
            string appId, string subjectId, CancellationToken ct) =>
            Task.FromResult(Snapshot(appId, subjectId));

        public Task<RoleServiceGrantResult> GrantAsync(
            string appId, string subjectId, string roleKey, string grantedBy,
            string idempotencyKey, CancellationToken ct)
        {
            GrantedRoles.Add(roleKey);
            var created = !_roles.Contains(roleKey, StringComparer.OrdinalIgnoreCase);
            if (created)
            {
                _roles.Add(roleKey);
                ActiveRole ??= roleKey;
            }

            return Task.FromResult(new RoleServiceGrantResult(created, Snapshot(appId, subjectId)));
        }

        public Task<RoleServiceRevokeResult> RevokeAsync(
            string appId, string subjectId, string roleKey, string revokedBy,
            string? reassignActiveRoleTo, string idempotencyKey, CancellationToken ct)
        {
            LastReassignTarget = reassignActiveRoleTo;
            _roles.RemoveAll(r => string.Equals(r, roleKey, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(ActiveRole, roleKey, StringComparison.OrdinalIgnoreCase))
            {
                ActiveRole = reassignActiveRoleTo;
            }

            return Task.FromResult(new RoleServiceRevokeResult(Snapshot(appId, subjectId)));
        }

        public Task<RoleServiceActiveRoleResult> SetActiveRoleAsync(
            string appId, string subjectId, string roleKey, string setBy,
            string idempotencyKey, CancellationToken ct)
        {
            if (!_roles.Contains(roleKey, StringComparer.OrdinalIgnoreCase))
            {
                throw new RoleServiceCallException("active-role", 409, "role.active_role_not_held");
            }

            SetActiveRoles.Add(roleKey);
            ActiveRole = roleKey;
            return Task.FromResult(new RoleServiceActiveRoleResult(Snapshot(appId, subjectId)));
        }

        public Task<RoleServiceSubjectPage> ListByRoleAsync(
            string appId, string roleKey, string? status, string? cursor,
            int? limit, CancellationToken ct) =>
            Task.FromResult(new RoleServiceSubjectPage(Array.Empty<RoleServiceSubjectListItem>(), null));

        private RoleServiceSubjectRoles Snapshot(string appId, string subjectId) =>
            new(
                appId,
                subjectId,
                _roles.Select(r => new RoleServiceRoleGrant(r, null, null, null, null)).ToArray(),
                ActiveRole is null ? null : new RoleServiceActiveRole(ActiveRole, null, null));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
