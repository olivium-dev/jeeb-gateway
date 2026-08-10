using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Users;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// F3 (unregister-as-jeeber) — <c>POST /v1/users/me/role/unregister</c>. Covers the guard
/// order (not_a_jeeber -&gt; active_delivery -&gt; positive_wallet_balance -&gt; force-offline
/// -&gt; UM revoke), the strict no-partial-apply-on-502 contract, and idempotency.
/// </summary>
public sealed class UnregisterJeeberBffTests
{
    private const string ZeroBalanceWalletJson = """{ "wallets": [] }""";
    private const string PositiveBalanceWalletJson =
        """{ "wallets": [ { "currencyID": 1, "amount": 42.5, "isActive": true } ] }""";

    [Fact]
    public async Task Unregister_200_HappyPath_RevokesRole_And_FlipsActiveRole()
    {
        var userId = Guid.NewGuid().ToString();
        var um = new StubUm();
        using var factory = MakeFactory(um, ZeroBalanceWalletJson);
        SeedDualRoleJeeber(factory, userId);
        var http = AuthenticatedClient(factory, userId);

        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("client");
        doc.RootElement.GetProperty("available_roles").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "client" });
        um.RemoveCalls.Should().Be(1);
        um.LastRemovedRole.Should().Be(Roles.Jeeber);

        var profile = await um.GetUserRolesAsync(userId, CancellationToken.None);
        profile!.AvailableRoles.Should().NotContain(Roles.Jeeber);
        profile.ActiveRole.Should().Be(Roles.Client);
    }

    [Fact]
    public async Task Unregister_404_WhenUserDoesNotHoldJeeberRole()
    {
        var userId = Guid.NewGuid().ToString();
        var um = new StubUm();
        using var factory = MakeFactory(um, ZeroBalanceWalletJson);
        SeedUser(factory, userId, Roles.Client);
        var http = AuthenticatedClient(factory, userId);

        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().EndWith("not_a_jeeber");
        um.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Unregister_409_ActiveDelivery_ShortCircuits_BeforeAnyUmCall()
    {
        var userId = Guid.NewGuid().ToString();
        var clientId = $"client-for-{userId}";
        var um = new StubUm();
        using var factory = MakeFactory(um, ZeroBalanceWalletJson);
        SeedDualRoleJeeber(factory, userId);

        var requests = factory.Services.GetRequiredService<IRequestsStore>();
        var request = await requests.CreateAsync(
            new CreateRequestInput { ClientId = clientId, Description = "In flight" }, CancellationToken.None);
        await requests.TryAcceptByJeeberAsync(request.Id, userId, 5, DateTimeOffset.UtcNow, CancellationToken.None);

        var http = AuthenticatedClient(factory, userId);
        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().EndWith("active_delivery");
        um.RemoveCalls.Should().Be(0, "the guard must short-circuit before any UM call");

        (await um.GetUserRolesAsync(userId, CancellationToken.None))!
            .AvailableRoles.Should().Contain(Roles.Jeeber);
    }

    [Fact]
    public async Task Unregister_409_PositiveWalletBalance_ShortCircuits_BeforeAnyUmCall()
    {
        var userId = Guid.NewGuid().ToString();
        var um = new StubUm();
        using var factory = MakeFactory(um, PositiveBalanceWalletJson);
        SeedDualRoleJeeber(factory, userId);
        var http = AuthenticatedClient(factory, userId);

        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().EndWith("positive_wallet_balance");
        um.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Unregister_502_UmCallFails_LocalProjection_Unchanged_NoPartialApply()
    {
        var userId = Guid.NewGuid().ToString();
        var um = new StubUm { RemoveThrows = new UserManagementCallException("role/revoke", 404) };
        using var factory = MakeFactory(um, ZeroBalanceWalletJson);
        SeedDualRoleJeeber(factory, userId);
        var http = AuthenticatedClient(factory, userId);

        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().EndWith("upstream_fault");

        var profile = await um.GetUserRolesAsync(userId, CancellationToken.None);
        profile!.AvailableRoles.Should().Contain(
            Roles.Jeeber, "a failed UM call must not partially apply the revoke");
        profile.ActiveRole.Should().Be(Roles.Jeeber);
    }

    [Fact]
    public async Task Unregister_Idempotent_SecondCall_Is_404_NotA500()
    {
        var userId = Guid.NewGuid().ToString();
        var um = new StubUm();
        using var factory = MakeFactory(um, ZeroBalanceWalletJson);
        SeedDualRoleJeeber(factory, userId);
        var http = AuthenticatedClient(factory, userId);

        var first = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
        um.RemoveCalls.Should().Be(1, "the second call must not re-dial UM");
    }

    [Fact]
    public async Task Unregister_Unauthenticated_Returns_401()
    {
        using var factory = MakeFactory(new StubUm(), ZeroBalanceWalletJson);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/users/me/role/unregister", EmptyBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- harness ----

    private static WebApplicationFactory<Program> MakeFactory(IUserManagementDualRoleClient um, string walletJson)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);

                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton(um);

                services.RemoveAll<ServiceWalletClient>();
                var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(walletJson, Encoding.UTF8, "application/json")
                });
                services.AddScoped(_ =>
                    new ServiceWalletClient("http://wallet.test/", new HttpClient(stub) { BaseAddress = new Uri("http://wallet.test/") }));

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;
                });
            });
        });

    private static void SeedDualRoleJeeber(WebApplicationFactory<Program> factory, string userId)
        => SeedUser(factory, userId, Roles.Client, Roles.Jeeber);

    private static void SeedUser(WebApplicationFactory<Program> factory, string userId, params string[] roles)
    {
        var owner = factory.Services.GetRequiredService<IUserManagementDualRoleClient>()
            .Should().BeOfType<StubUm>().Subject;
        owner.Seed(
            userId,
            roles,
            roles.Contains(Roles.Jeeber) ? Roles.Jeeber : Roles.Client);
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory, string userId)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, userId, Roles.Client, Roles.Jeeber));
        return http;
    }

    private static string MintGatewayBearer(WebApplicationFactory<Program> factory, string userId, params string[] roles)
    {
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var signingKey = config["Jwt:SigningKey"]!;
        var issuer = config["Jwt:Issuer"]!;
        var audience = config["Jwt:Audience"]!;

        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", userId),
            new(System.Security.Claims.ClaimTypes.Sid, userId),
        };
        claims.Add(new System.Security.Claims.Claim("active_role", Roles.Jeeber));
        foreach (var r in roles) claims.Add(new System.Security.Claims.Claim("roles", r));

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static StringContent EmptyBody() => new("{}", Encoding.UTF8, "application/json");

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private sealed class StubUm : IUserManagementDualRoleClient
    {
        private readonly Dictionary<string, UserRolesResult> _roles =
            new(StringComparer.Ordinal);

        public int RemoveCalls { get; private set; }
        public string? LastRemovedRole { get; private set; }
        public Exception? RemoveThrows { get; init; }

        public void Seed(
            string userId,
            IReadOnlyList<string> roles,
            string activeRole)
            => _roles[userId] = new UserRolesResult(
                userId, roles.ToArray(), activeRole);

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(new PhoneFindOrCreateResult(phone, false, new[] { Roles.Client }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleSwitchReissueResult(userId, "a", "r", opaqueRole));

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, true));

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            RemoveCalls++;
            LastRemovedRole = opaqueRole;
            if (RemoveThrows is not null) throw RemoveThrows;
            var current = _roles[userId];
            var roles = current.AvailableRoles
                .Where(role => !string.Equals(
                    role, opaqueRole, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _roles[userId] = new UserRolesResult(
                userId,
                roles,
                roles.Contains(current.ActiveRole, StringComparer.OrdinalIgnoreCase)
                    ? current.ActiveRole
                    : roles.FirstOrDefault());
            return Task.FromResult(new RoleGrantResult(userId, roles, true));
        }

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult(_roles.TryGetValue(userId, out var roles)
                ? roles
                : null);
    }
}
