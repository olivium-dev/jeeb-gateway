using FluentAssertions;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>
/// KYC approve granted the jeeber role but left <c>active_role = customer</c> persisted, so the
/// approved jeeber's sessions stayed client-scoped (client surface + every jeeber push deep link
/// refused). These pin the grant → active-role promotion and its fail-soft contract.
/// </summary>
public class JeeberActiveRolePromotionTests
{
    private const string UserId = "d1000000-0000-4000-8000-000000000002";

    [Fact]
    public async Task Promotes_ActiveRole_When_The_Grant_Confirms_The_Jeeber_Role()
    {
        var um = new RecordingDualRoleClient(activeRoleAfterSwitch: Roles.Jeeber);

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Jeeber,
            new RoleGrantResult(UserId, new[] { Roles.Client, Roles.Jeeber }, Added: true),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeTrue();
        um.SwitchCalls.Should().Be(1);
        um.LastUserId.Should().Be(UserId);
        um.LastRole.Should().Be(Roles.Jeeber, "user-management stores the OPAQUE role vocabulary");
    }

    [Fact]
    public async Task Promotes_On_Reapproval_Even_When_The_Role_Was_Already_Held()
    {
        // Added=false is the set-semantics no-op re-approval; it is exactly the state an
        // already-granted-but-never-activated jeeber is stuck in, so it MUST still promote.
        var um = new RecordingDualRoleClient(activeRoleAfterSwitch: Roles.Jeeber);

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Jeeber,
            new RoleGrantResult(UserId, new[] { Roles.Client, Roles.Jeeber }, Added: false),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeTrue();
        um.SwitchCalls.Should().Be(1);
    }

    [Fact]
    public async Task Does_Not_Touch_The_Active_Role_For_A_NonJeeber_Grant()
    {
        var um = new RecordingDualRoleClient(activeRoleAfterSwitch: Roles.Jeeber);

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Client,
            new RoleGrantResult(UserId, new[] { Roles.Client }, Added: true),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeFalse();
        um.SwitchCalls.Should().Be(0, "only the jeeber grant moves the active role");
    }

    [Fact]
    public async Task Never_Promotes_A_Role_The_Grant_Did_Not_Confirm()
    {
        var um = new RecordingDualRoleClient(activeRoleAfterSwitch: Roles.Jeeber);

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Jeeber,
            new RoleGrantResult(UserId, new[] { Roles.Client }, Added: false),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeFalse();
        um.SwitchCalls.Should().Be(0, "the gateway must never assert authority UM did not return");
    }

    [Fact]
    public async Task A_UserManagement_Fault_Is_Swallowed_So_The_Approve_Still_Stands()
    {
        var um = new RecordingDualRoleClient(
            activeRoleAfterSwitch: Roles.Jeeber,
            throws: new UserManagementCallException("role/switch", 502));

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Jeeber,
            new RoleGrantResult(UserId, new[] { Roles.Client, Roles.Jeeber }, Added: true),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeFalse("a blip defers the promotion; it never rolls the approve back");
    }

    [Fact]
    public async Task Reports_False_When_UserManagement_Persisted_Some_Other_Role()
    {
        var um = new RecordingDualRoleClient(activeRoleAfterSwitch: Roles.Client);

        var promoted = await JeeberActiveRolePromotion.PromoteAsync(
            um, UserId, Roles.Jeeber,
            new RoleGrantResult(UserId, new[] { Roles.Client, Roles.Jeeber }, Added: true),
            NullLogger.Instance, CancellationToken.None);

        promoted.Should().BeFalse("the result is read back, not assumed");
    }

    private sealed class RecordingDualRoleClient : IUserManagementDualRoleClient
    {
        private readonly string _activeRoleAfterSwitch;
        private readonly Exception? _throws;

        public RecordingDualRoleClient(string activeRoleAfterSwitch, Exception? throws = null)
        {
            _activeRoleAfterSwitch = activeRoleAfterSwitch;
            _throws = throws;
        }

        public int SwitchCalls { get; private set; }
        public string? LastUserId { get; private set; }
        public string? LastRole { get; private set; }

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            SwitchCalls++;
            LastUserId = userId;
            LastRole = opaqueRole;
            if (_throws is not null) throw _throws;
            return Task.FromResult(new RoleSwitchReissueResult(userId, "a", "r", _activeRoleAfterSwitch));
        }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
