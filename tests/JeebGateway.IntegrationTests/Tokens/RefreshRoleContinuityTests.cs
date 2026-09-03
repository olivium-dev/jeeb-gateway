using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

/// <summary>G5 — rotation re-resolved roles from a process-RAM store any restart empties, minting
/// valid roles-less tokens that 403 at L2 on every route. Pins the snapshot and the fail-closed 401.</summary>
public class RefreshRoleContinuityTests
{
    private const string Key = "refresh-role-continuity-signing-key-at-least-32-bytes!!";

    [Fact]
    public async Task Refresh_PreservesMintedRoles_WhenUsersStoreHasNoProfile()
    {
        // The exact staging shape: super-login minted {customer, driver} and wrote no local
        // profile, so the store resolves nothing for this user.
        var svc = NewService(new EmptyUsersStoreAdapter());

        var pair = await svc.IssueAsync(
            "d1000000-0000-4000-8000-000000000002",
            new[] { "customer", "driver" },
            "driver",
            authentication: null,
            CancellationToken.None);

        var refreshed = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.Ok);
        RolesOf(refreshed.Tokens!.AccessToken).Should().BeEquivalentTo(
            new[] { "customer", "driver" },
            "a rotation must not drop the roles the session was minted with");
        ActiveRoleOf(refreshed.Tokens.AccessToken).Should().Be("driver");
    }

    [Fact]
    public async Task Refresh_PreservesMintedRoles_AcrossRepeatedRotations()
    {
        var svc = NewService(new EmptyUsersStoreAdapter());

        var current = await svc.IssueAsync(
            "u-multi", new[] { "customer", "driver" }, "driver",
            authentication: null, CancellationToken.None);

        // 15-minute access tokens mean an unattended session rotates several times an hour;
        // the context must survive every hop, not just the first.
        for (var i = 0; i < 5; i++)
        {
            var result = await svc.RefreshAsync(current.RefreshToken, CancellationToken.None);
            result.Outcome.Should().Be(RefreshOutcome.Ok, $"rotation {i + 1} must succeed");
            RolesOf(result.Tokens!.AccessToken).Should().BeEquivalentTo(new[] { "customer", "driver" });
            current = result.Tokens;
        }
    }

    [Fact]
    public async Task Refresh_PrefersMintedRoles_OverAStoreThatDowngradesThem()
    {
        // The documented "roles=customer trap": the local profile shell defaults to a single
        // customer role, which silently demoted a jeeber's session on its first rotation.
        var svc = NewService(new FixedUsersStoreAdapter(new[] { "customer" }, "customer"));

        var pair = await svc.IssueAsync(
            "u-jeeber", new[] { "customer", "driver" }, "driver",
            authentication: null, CancellationToken.None);

        var refreshed = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.Ok);
        RolesOf(refreshed.Tokens!.AccessToken).Should().Contain("driver",
            "the jeeber capability must survive an unattended rotation");
    }

    [Fact]
    public async Task Refresh_FailsClosed_WhenNoRolesCanBeResolved()
    {
        // A legacy record (minted before the snapshot existed) whose user the store cannot
        // resolve. Fail closed: a 401 re-login beats a permanently 403-ing session.
        var svc = NewService(new EmptyUsersStoreAdapter(), new LegacyRecordStore());

        var pair = await svc.IssueAsync("u-legacy", new[] { "customer" }, CancellationToken.None);

        var refreshed = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.RoleResolutionFailed);
        refreshed.Tokens.Should().BeNull("a roles-less token 403s on every capability route");
    }

    [Fact]
    public async Task Refresh_LegacyRecord_StillFallsBackToTheStore()
    {
        // Records written before this change carry no snapshot; they must keep working.
        var svc = NewService(
            new FixedUsersStoreAdapter(new[] { "customer" }, "customer"), new LegacyRecordStore());

        var pair = await svc.IssueAsync("u-legacy-ok", new[] { "customer" }, CancellationToken.None);

        var refreshed = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.Ok);
        RolesOf(refreshed.Tokens!.AccessToken).Should().BeEquivalentTo(new[] { "customer" });
    }

    [Fact]
    public async Task Refresh_WithARoleResolver_StillWins_OverTheSnapshot()
    {
        // The admin ceremony re-resolves roles live on every rotation; that authority must keep
        // precedence over anything recorded at mint time.
        var svc = NewService(new EmptyUsersStoreAdapter());

        var pair = await svc.IssueAsync(
            "u-admin", new[] { "customer" }, "customer",
            authentication: null, CancellationToken.None);

        var refreshed = await svc.RefreshAsync(
            pair.RefreshToken,
            (_, _) => Task.FromResult<TokenRoleContext?>(
                new TokenRoleContext(new[] { "operations_admin" }, "operations_admin")),
            CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.Ok);
        RolesOf(refreshed.Tokens!.AccessToken).Should().BeEquivalentTo(new[] { "operations_admin" });
    }

    private static IReadOnlyList<string> RolesOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken)
            .Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToArray();

    private static string? ActiveRoleOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken)
            .Claims.FirstOrDefault(c => c.Type == "active_role")?.Value;

    private static TokenService NewService(
        IUsersStoreAdapter users, IRefreshTokenStore? store = null) =>
        new(
            store ?? new InMemoryRefreshTokenStore(),
            users,
            Options.Create(new JwtOptions
            {
                Issuer = "jeeb-gateway",
                Audience = "jeeb-clients",
                SigningKey = Key,
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            }),
            TimeProvider.System);

    /// <summary>Models records written BEFORE the snapshot existed: normal in every other way,
    /// but nothing ever reads a SessionRoleSnapshot back.</summary>
    private sealed class LegacyRecordStore : IRefreshTokenStore
    {
        private readonly InMemoryRefreshTokenStore _inner = new();

        public Task AddAsync(RefreshToken token, CancellationToken ct) => _inner.AddAsync(token, ct);

        public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
        {
            var current = await _inner.FindByHashAsync(tokenHash, ct);
            return current is null
                ? null
                : new RefreshToken
                {
                    TokenId = current.TokenId,
                    UserId = current.UserId,
                    TokenHash = current.TokenHash,
                    IssuedAt = current.IssuedAt,
                    ExpiresAt = current.ExpiresAt,
                    RevokedAt = current.RevokedAt,
                    RevokedReason = current.RevokedReason,
                    ReplacedByTokenId = current.ReplacedByTokenId,
                };
        }

        public Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct) =>
            _inner.RotateAsync(oldTokenId, replacement, ct);

        public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct) =>
            _inner.RevokeAsync(tokenId, reason, ct);

        public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct) =>
            _inner.RevokeAllForUserAsync(userId, reason, ct);

        public Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct) =>
            _inner.RevokeChainAsync(startTokenId, reason, ct);
    }

    /// <summary>The super-login shape: the gateway never created a local profile for this user.</summary>
    private sealed class EmptyUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            Task.FromResult("client");
    }

    private sealed class FixedUsersStoreAdapter : IUsersStoreAdapter
    {
        private readonly IReadOnlyList<string> _roles;
        private readonly string _activeRole;

        public FixedUsersStoreAdapter(IReadOnlyList<string> roles, string activeRole)
        {
            _roles = roles;
            _activeRole = activeRole;
        }

        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            Task.FromResult(_roles);

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            Task.FromResult(_activeRole);
    }
}
