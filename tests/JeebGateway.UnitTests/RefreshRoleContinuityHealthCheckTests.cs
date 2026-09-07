using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Health;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>Readiness requires current authoritative owner evidence.</summary>
public sealed class RefreshRoleContinuityHealthCheckTests
{
    [Fact]
    public async Task Degraded_WhenOwnerUnavailableWhileSessionsAreRotating()
    {
        // Existing families cannot make an unavailable owner healthy.
        var census = new InProcessRefreshSessionCensus();
        census.RecordRotation("user-1");
        census.RecordRotation("user-2");

        var result = await CheckAsync(new StubAuthority(available: false), census);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("unavailable");
        result.Data["refreshFamiliesObserved"].Should().Be(2);
        result.Data["roleSource"].Should().Be("user-management-live");
        result.Data["suspensionSource"].Should().Be("ban-service-live");
    }

    [Fact]
    public async Task Healthy_WhenOwnerReadPathIsVerified()
    {
        var census = new InProcessRefreshSessionCensus();
        census.RecordRotation("user-1");

        var result = await CheckAsync(new StubAuthority(available: true), census);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("verified");
    }

    [Fact]
    public async Task Healthy_OnAFreshProcessWithNoRotationsYet()
    {
        // Cold boot is healthy only after the owner read path succeeds.
        var result = await CheckAsync(new StubAuthority(available: true), new InProcessRefreshSessionCensus());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["refreshFamiliesObserved"].Should().Be(0);
    }

    [Fact]
    public async Task Surfaces_TheRolesEmptyCounterAndItsLastOccurrence()
    {
        var census = new InProcessRefreshSessionCensus();
        census.RecordRotation("user-1");
        var at = new DateTimeOffset(2026, 9, 4, 9, 12, 31, TimeSpan.Zero);
        census.RecordRolesEmptyRefresh(at);
        census.RecordRolesEmptyRefresh(at.AddSeconds(3));

        var result = await CheckAsync(new StubAuthority(available: false), census);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["rolesEmptyRefreshes"].Should().Be(2);
        result.Data["lastRolesEmptyAt"].Should().Be(
            at.AddSeconds(3).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Degraded_NotUnhealthy_WhenOwnerProbeThrows()
    {
        // Unhealthy would restart-loop the container via the Dockerfile HEALTHCHECK.
        var result = await CheckAsync(new ThrowingUsersStore(), new InProcessRefreshSessionCensus());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("unavailable");
    }

    [Fact]
    public void Census_CountsDistinctSessionsOnly_AndIsBounded()
    {
        var census = new InProcessRefreshSessionCensus();
        census.RecordRotation("user-1");
        census.RecordRotation("user-1");
        census.RecordRotation("");
        census.RecordRotation("user-2");

        census.ActiveFamilies.Should().Be(2, "repeat rotations of one session are one session");
        census.RolesEmptyRefreshes.Should().Be(0);
        census.LastRolesEmptyAt.Should().BeNull();
    }

    [Fact]
    public void TheRowIsOnTheDeclaredReadyRoster()
    {
        GatewayHealthRoster.Ready.Should().Contain("refresh-role-continuity");
        GatewayHealthRoster.Ready.Should().HaveCount(GatewayHealthRoster.ExpectedReadyCount);
        GatewayHealthRoster.ExpectedReadyCount.Should().Be(28);
    }

    [Fact]
    public async Task TheProfileCount_IsReadFromTheRealUsersStore()
    {
        // Wiring proof: the number the row reports is the store's own total, not a guess.
        var store = new InMemoryUsersStore();
        var census = new UsersStoreCensus(store);

        (await census.CountProfilesAsync(CancellationToken.None)).Should().Be(0);

        await store.GetOrCreateAsync("u-1", CancellationToken.None);
        await store.GetOrCreateAsync("u-2", CancellationToken.None);
        await store.GetOrCreateAsync("u-1", CancellationToken.None);

        (await census.CountProfilesAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task TheFailClosedRefresh_EmitsTheGrepableLine_AndFeedsTheRow()
    {
        // D2 sec 4b: the class must be recognisable in 30 s. Pins the log line and the counter.
        var logs = new CapturingLogger();
        var census = new InProcessRefreshSessionCensus();
        var service = new TokenService(
            new SnapshotStrippingStore(),
            new EmptyUsersStoreAdapter(),
            Options.Create(new JwtOptions
            {
                Issuer = "jeeb-gateway",
                Audience = "jeeb-clients",
                SigningKey = "refresh-role-continuity-alarm-signing-key-32-bytes!!",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            }),
            TimeProvider.System,
            new TestRefreshRoleAuthority(Array.Empty<string>(), ""),
            logs,
            census);

        var pair = await service.IssueAsync("u-1", new[] { "customer" }, CancellationToken.None);
        var result = await service.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        result.Outcome.Should().Be(RefreshOutcome.RoleResolutionFailed);
        logs.Messages.Should().ContainSingle(m =>
            m.Contains("token_mint.roles_empty") && m.Contains("path=refresh")
            && m.Contains("source=owner_authority"));
        logs.Messages.Should().NotContain(m => m.Contains("u-1"), "never log the user id");
        census.RolesEmptyRefreshes.Should().Be(1);
        census.ActiveFamilies.Should().Be(1);

        var row = await CheckAsync(new StubAuthority(available: false), census);
        row.Status.Should().Be(HealthStatus.Degraded);
        row.Data["rolesEmptyRefreshes"].Should().Be(1);
    }

    private static Task<HealthCheckResult> CheckAsync(IRefreshRoleAuthority users, IRefreshSessionCensus census) =>
        new RefreshRoleContinuityHealthCheck(users, census)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    private sealed class StubAuthority(bool available) : IRefreshRoleAuthority
    {
        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(available);
        public Task<TokenRoleContext?> ResolveAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingUsersStore : IRefreshRoleAuthority
    {
        public Task<bool> ProbeAsync(CancellationToken ct) =>
            throw new InvalidOperationException("owner unavailable");
        public Task<TokenRoleContext?> ResolveAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class EmptyUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            Task.FromResult("client");
    }

    /// <summary>Models pre-G5 records, which carry no SessionRoleSnapshot.</summary>
    private sealed class SnapshotStrippingStore : IRefreshTokenStore
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

    private sealed class CapturingLogger : ILogger<TokenService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
