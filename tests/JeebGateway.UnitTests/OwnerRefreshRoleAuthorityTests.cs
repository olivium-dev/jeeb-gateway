using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Health;
using JeebGateway.Tokens;
using JeebGateway.Users.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Um = JeebGateway.service.ServiceUserManagement;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class OwnerRefreshRoleAuthorityTests
{
    private const string UserId = "10000000-0000-4000-8000-000000000001";
    private const string Valid = "{\"userId\":\"" + UserId + "\",\"available_roles\":[\"customer\",\"driver\"],\"active_role\":\"driver\"}";
    private const string Absent = "{\"type\":\"https://docs.olivium-dev.com/errors/user-not-found\",\"status\":404}";

    [Fact]
    public async Task FreshProcessUsesOwnerRolesWithoutAnyLocalProjectionOrAccessBearer()
    {
        var retainedStore = new InMemoryRefreshTokenStore();
        using var before = new Fixture();
        var pair = await Service(before.Authority, retainedStore).IssueAsync(
            UserId, ["customer", "driver"], "driver", null, default);
        // The service, scope factory and local adapter are all new. Only the
        // persisted-record seam survives; this does not claim a real DB restart.
        using var after = new Fixture();
        var result = await Service(after.Authority, retainedStore).RefreshAsync(pair.RefreshToken, default);
        result.Outcome.Should().Be(RefreshOutcome.Ok);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Tokens!.AccessToken);
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value)
            .Should().BeEquivalentTo(["customer", "driver"]);
        jwt.Claims.Single(c => c.Type == "active_role").Value.Should().Be("driver");
        after.Http.Paths.Should().Equal($"/api/User/{UserId}/roles");
        after.Http.AuthorizationSeen.Should().BeFalse();
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(503)]
    public async Task MissingOrUnavailableOwnerNeverMintsFromSnapshot(int status)
    {
        using var fixture = new Fixture();
        var service = Service(fixture.Authority);
        var pair = await service.IssueAsync(UserId, ["customer", "driver"], "driver", null, default);
        fixture.Http.Status = (HttpStatusCode)status;
        fixture.Http.Body = Absent;
        var result = await service.RefreshAsync(pair.RefreshToken, default);
        result.Outcome.Should().Be(RefreshOutcome.RoleResolutionFailed);
        result.Tokens.Should().BeNull();
        fixture.Http.Status = HttpStatusCode.OK;
        fixture.Http.Body = Valid;
        (await service.RefreshAsync(pair.RefreshToken, default)).Outcome.Should().Be(RefreshOutcome.Ok,
            "a failed authority read must not consume the existing refresh token");
    }

    [Fact]
    public async Task LegacyRetainedRecordWithoutSnapshotResolvesFromOwner()
    {
        using var fixture = new Fixture();
        var store = new InMemoryRefreshTokenStore();
        const string raw = "legacy-refresh-test-fixture";
        await store.AddAsync(new RefreshToken {
            TokenId = Guid.NewGuid().ToString(), UserId = UserId,
            TokenHash = TokenService.HashToken(raw), IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }, default);
        (await Service(fixture.Authority, store).RefreshAsync(raw, default)).Outcome
            .Should().Be(RefreshOutcome.Ok);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"userId\":\"10000000-0000-4000-8000-000000000002\",\"available_roles\":[\"driver\"],\"active_role\":\"driver\"}")]
    [InlineData("{\"userId\":\"10000000-0000-4000-8000-000000000001\",\"available_roles\":[],\"active_role\":\"customer\"}")]
    [InlineData("{\"userId\":\"10000000-0000-4000-8000-000000000001\",\"available_roles\":[\"customer\"],\"active_role\":\"driver\"}")]
    public async Task InvalidIdentityOrStaleActiveRoleHasNoDefault(string payload)
    {
        using var fixture = new Fixture();
        fixture.Http.Body = payload;
        (await fixture.Authority.ResolveAsync(UserId, default)).Should().BeNull();
    }

    [Fact]
    public async Task RevocationAndSuspensionAreReadAgainOnEveryRotation()
    {
        using var fixture = new Fixture();
        var service = Service(fixture.Authority);
        var pair = await service.IssueAsync(UserId, ["customer", "driver"], "driver", null, default);
        var first = await service.RefreshAsync(pair.RefreshToken, default);
        first.Outcome.Should().Be(RefreshOutcome.Ok);
        fixture.Http.Body = Valid.Replace("[\"customer\",\"driver\"]", "[\"customer\"]")
            .Replace("\"active_role\":\"driver\"", "\"active_role\":\"customer\"");
        (await service.RefreshAsync(first.Tokens!.RefreshToken, default)).Outcome
            .Should().Be(RefreshOutcome.RoleResolutionFailed);
        fixture.Http.Body = Valid;
        fixture.Suspensions.Suspended = true;
        (await service.RefreshAsync(first.Tokens.RefreshToken, default)).Outcome
            .Should().Be(RefreshOutcome.RoleResolutionFailed);
        fixture.Suspensions.Suspended = false;
        fixture.Suspensions.Unavailable = true;
        (await service.RefreshAsync(first.Tokens.RefreshToken, default)).Outcome
            .Should().Be(RefreshOutcome.RoleResolutionFailed);
    }

    [Fact]
    public async Task OwnerTimeoutFailsClosedAndCallerCancellationPropagates()
    {
        using var fixture = new Fixture();
        fixture.Http.Timeout = true;
        (await fixture.Authority.ResolveAsync(UserId, default)).Should().BeNull();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Authority.ResolveAsync(UserId, cancellation.Token));
    }

    [Fact]
    public async Task ReadinessRequiresExactOwnerResponseAndLiveSuspensionPath()
    {
        using var fixture = new Fixture();
        fixture.Http.Status = HttpStatusCode.NotFound;
        fixture.Http.Body = Absent;
        var census = new InProcessRefreshSessionCensus();
        census.RecordRotation("existing-family");
        var health = new RefreshRoleContinuityHealthCheck(fixture.Authority, census);
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
        fixture.Http.Body = "{\"status\":404,\"type\":\"wrong-route\"}";
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
        fixture.Http.Body = Absent;
        fixture.Suspensions.Unavailable = true;
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
        fixture.Http.Status = HttpStatusCode.Unauthorized;
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
    }

    private static TokenService Service(IRefreshRoleAuthority authority, IRefreshTokenStore? store = null) =>
        new(store ?? new InMemoryRefreshTokenStore(), new RejectLocalRoles(),
            Options.Create(new JwtOptions { SigningKey = "owner-role-test-key-at-least-thirty-two-bytes", Issuer = "jeeb-gateway", Audience = "jeeb-clients" }),
            TimeProvider.System, roleAuthority: authority);

    private sealed class Fixture : IDisposable
    {
        public OwnerHttp Http { get; } = new();
        public SuspensionSource Suspensions { get; } = new();
        private readonly ServiceProvider _services;
        public OwnerRefreshRoleAuthority Authority { get; }
        public Fixture()
        {
            _services = new ServiceCollection()
                .AddScoped(_ => new Um.ServiceUserManagementClient("https://um.invalid/", new HttpClient(Http, false)))
                .AddSingleton<IUserSuspensionSource>(Suspensions).BuildServiceProvider();
            Authority = new OwnerRefreshRoleAuthority(_services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OwnerRefreshRoleAuthority>.Instance);
        }
        public void Dispose() { _services.Dispose(); Http.Dispose(); }
    }

    private sealed class OwnerHttp : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = Valid;
        public bool Timeout;
        public bool AuthorizationSeen;
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Timeout) throw new TaskCanceledException("owner timeout fixture");
            Paths.Add(request.RequestUri!.AbsolutePath);
            AuthorizationSeen |= request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class SuspensionSource : IUserSuspensionSource
    {
        public bool Suspended;
        public bool Unavailable;
        public Task<UserSuspension> ReadAsync(string userId, CancellationToken ct) =>
            Unavailable ? throw new HttpRequestException("ban down fixture") : Task.FromResult(new UserSuspension(Suspended, null));
    }
    private sealed class RejectLocalRoles : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) => throw new InvalidOperationException("must not read local roles");
        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) => throw new InvalidOperationException("must not read local active role");
    }
    private sealed class RejectCensus : IUsersStoreCensus
    {
        public Task<int> CountProfilesAsync(CancellationToken ct) => throw new InvalidOperationException("RAM cannot attest owner readiness");
    }
}
