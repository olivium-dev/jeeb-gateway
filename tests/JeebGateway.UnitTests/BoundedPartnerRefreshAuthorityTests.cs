using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using JeebGateway.Partner.Auth;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Tokens;
using JeebGateway.Users.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class BoundedPartnerRefreshAuthorityTests
{
    [Theory]
    [InlineData("")]
    [InlineData(":active")]
    [InlineData(":used")]
    [InlineData(":session")]
    public async Task MissingOwnerRecordCannotRefreshFromBoundedSnapshot(string suffix)
    {
        using var fixture = new Fixture();
        var raw = await fixture.LoginAsync();
        fixture.Kv.HiddenKey = Fixture.ReservationKey + suffix;
        var result = await fixture.Tokens.RefreshAsync(raw, default);
        result.Outcome.Should().Be(RefreshOutcome.RoleResolutionFailed);
        result.Tokens.Should().BeNull();
        fixture.Moderation.Reads.Should().Be(0, "owner attestation must precede moderation");
    }

    [Fact]
    public async Task DurableCredentialTombstoneRejectsEvenWithoutRefreshFamilyRevocation()
    {
        using var fixture = new Fixture();
        var raw = await fixture.LoginAsync();
        await fixture.Owner.RemoveAsync(Fixture.Login, Fixture.Holder, default);
        var result = await fixture.Tokens.RefreshAsync(raw, default);
        result.Outcome.Should().Be(RefreshOutcome.RoleResolutionFailed);
        result.Tokens.Should().BeNull();
        fixture.Moderation.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerReadFailurePreservesRefreshForRecovery(bool timeout)
    {
        using var fixture = new Fixture();
        var raw = await fixture.LoginAsync();
        fixture.Kv.ReadFailure = timeout
            ? new TaskCanceledException("owner timeout fixture")
            : new HttpRequestException("owner outage fixture");
        var result = await fixture.Tokens.RefreshAsync(raw, default);
        result.Outcome.Should().Be(RefreshOutcome.AuthorityUnavailable);
        result.Tokens.Should().BeNull();
        fixture.Moderation.Reads.Should().Be(0);
        fixture.Kv.ReadFailure = null;
        await fixture.AssertSuccessfulRecoveryAsync(raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveBanFailureOrSuspensionCannotMint(bool unavailable)
    {
        using var fixture = new Fixture();
        var raw = await fixture.LoginAsync();
        fixture.Moderation.Unavailable = unavailable;
        fixture.Moderation.Suspended = !unavailable;
        var result = await fixture.Tokens.RefreshAsync(raw, default);
        result.Outcome.Should().Be(unavailable
            ? RefreshOutcome.AuthorityUnavailable : RefreshOutcome.RoleResolutionFailed);
        result.Tokens.Should().BeNull();
        fixture.Moderation.Reads.Should().Be(1);
        fixture.Moderation.Unavailable = fixture.Moderation.Suspended = false;
        await fixture.AssertSuccessfulRecoveryAsync(raw);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Holder = Guid.Parse("e1234567-1234-4234-8234-123456789abc");
        public static readonly string Login = "devtool-partner-" + Holder.ToString("N");
        public static readonly string ReservationKey = "dev-partner-credential:" + Holder.ToString("N");
        private readonly ServiceProvider _services;
        private readonly InMemoryRefreshTokenStore _refresh = new();
        public ControllableKv Kv { get; } = new();
        public ModerationSource Moderation { get; } = new();
        public PartnerCredentialStore Owner { get; }
        public TokenService Tokens { get; }
        private DateTimeOffset _deadline;

        public Fixture()
        {
            Owner = new PartnerCredentialStore(Options.Create(new PartnerAuthOptions()),
                Kv, TimeProvider.System, NullLogger<PartnerCredentialStore>.Instance);
            // No UM client or local-role authority is registered: bounded sessions
            // must succeed solely through the real runtime credential owner and Ban.
            _services = new ServiceCollection()
                .AddSingleton<IPartnerCredentialStore>(Owner)
                .AddSingleton<IUserSuspensionSource>(Moderation).BuildServiceProvider();
            var authority = new OwnerRefreshRoleAuthority(
                _services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OwnerRefreshRoleAuthority>.Instance);
            Tokens = new TokenService(_refresh, new RejectLocalRoles(),
                Options.Create(new JwtOptions {
                    SigningKey = new string('x', 64),
                    Issuer = "jeeb-gateway", Audience = "jeeb-clients"
                }), TimeProvider.System, authority);
        }

        public async Task<string> LoginAsync()
        {
            await Owner.ReserveRuntimeSeedAsync(Login, Holder, "Fixture", "fixture-secret", default);
            await Owner.ActivateRuntimeSeedAsync(Login, Holder, default);
            var account = await Owner.VerifyAsync(Login, "fixture-secret", default);
            account.Should().NotBeNull();
            _deadline = account!.RuntimeSessionExpiresAt!.Value;
            var pair = await Tokens.IssueBoundedAsync(Holder.ToString(),
                ["partner"], "partner", _deadline, default);
            var record = await _refresh.FindByHashAsync(TokenService.HashToken(pair.RefreshToken), default);
            await Owner.BindRuntimeSessionAsync(Login, Holder, record!.BoundedSessionFamilyId!, default);
            return pair.RefreshToken;
        }

        public async Task AssertSuccessfulRecoveryAsync(string raw)
        {
            var result = await Tokens.RefreshAsync(raw, default);
            result.Outcome.Should().Be(RefreshOutcome.Ok,
                "failed owner or moderation reads must not consume the refresh");
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Tokens!.AccessToken);
            jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal("partner");
            jwt.Claims.Single(c => c.Type == "active_role").Value.Should().Be("partner");
            var replacement = await _refresh.FindByHashAsync(
                TokenService.HashToken(result.Tokens.RefreshToken), default);
            replacement!.AbsoluteSessionExpiresAt.Should().Be(_deadline);
            replacement.ExpiresAt.Should().BeOnOrBefore(_deadline);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class ControllableKv : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new(TimeProvider.System);
        public string? HiddenKey;
        public Exception? ReadFailure;
        public Task<IdempotencyOutcome> PutOrGetAsync(string key, int statusCode,
            string responseBodyJson, int ttlSeconds, CancellationToken ct) =>
            _inner.PutOrGetAsync(key, statusCode, responseBodyJson, ttlSeconds, ct);
        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFailure is not null) throw ReadFailure;
            return key == HiddenKey ? Task.FromResult<IdempotencyOutcome?>(null) : _inner.GetAsync(key, ct);
        }
        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct) =>
            _inner.FindByPrefixAsync(prefix, ct);
    }

    private sealed class ModerationSource : IUserSuspensionSource
    {
        public bool Unavailable;
        public bool Suspended;
        public int Reads;
        public Task<UserSuspension> ReadAsync(string userId, CancellationToken ct)
        {
            userId.Should().Be(Fixture.Holder.ToString());
            Reads++;
            if (Unavailable) throw new HttpRequestException("Ban outage fixture");
            return Task.FromResult(new UserSuspension(Suspended, null));
        }
    }

    private sealed class RejectLocalRoles : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("bounded refresh cannot consult local roles");
        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("bounded refresh cannot consult local active role");
    }
}
