using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using GetAllUsersResponse = JeebGateway.service.ServiceUserManagement.GetAllUsersResponse;
using UserProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-8 — GET /api/User/super-login/users. The FULL Super-Login+ picker roster
/// (every UM user, not just the 3 seeded demo rows), sourced from user-management's
/// own list API via <see cref="UmClient.AllAsync(int?,int?,bool?,System.Threading.CancellationToken)"/>.
/// Covers: roster shape, no-passcode-leak, pagination aggregation, name fallback, and
/// the SEC-13 flag gate (OpenMode + DemoUsers:Enabled) that must 404 the surface off.
/// </summary>
public class SuperLoginUsersEndpointTests
{
    private const string Path = "/api/User/super-login/users";

    private static WebApplicationFactory<Program> MakeFactory(
        UmClient um, bool openMode = true, bool demoUsersEnabled = true)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SuperLogin:OpenMode"] = openMode ? "true" : "false",
                    ["DemoUsers:Enabled"] = demoUsersEnabled ? "true" : "false",
                    ["Security:RateLimit:Enabled"] = "false",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
            });
        });

    [Fact]
    public async Task Returns_full_roster_shape_with_name_and_role_and_no_passcode()
    {
        var um = new StubUmClient(new[]
        {
            Row("11111111-0000-0000-0000-000000000001", "Nour", active: "customer", available: new[] { "customer" }),
            Row("22222222-0000-0000-0000-000000000002", "Karim", active: "driver", available: new[] { "customer", "driver" }),
        });

        var resp = await MakeFactory(um).CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await resp.Content.ReadAsStringAsync();
        // NO-PASSCODE-LEAK: the real-user roster must never carry a passcode field.
        raw.Should().NotContain("passcode");

        using var doc = JsonDocument.Parse(raw);
        var users = doc.RootElement.GetProperty("users");
        users.GetArrayLength().Should().Be(2);

        var first = users[0];
        first.GetProperty("userId").GetString().Should().Be("11111111-0000-0000-0000-000000000001");
        first.GetProperty("name").GetString().Should().Be("Nour");
        first.GetProperty("role").GetString().Should().Be("customer");
        first.GetProperty("roles").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("customer");
        first.TryGetProperty("passcode", out _).Should().BeFalse();

        var second = users[1];
        second.GetProperty("role").GetString().Should().Be("driver");
        second.GetProperty("roles").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Name_falls_back_to_userId_when_username_blank_and_role_defaults_to_client()
    {
        var um = new StubUmClient(new[]
        {
            Row("33333333-0000-0000-0000-000000000003", username: null, active: null, available: System.Array.Empty<string>()),
        });

        var resp = await MakeFactory(um).CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("users")[0];
        user.GetProperty("name").GetString().Should().Be("33333333-0000-0000-0000-000000000003");
        user.GetProperty("role").GetString().Should().Be("client");
    }

    [Fact]
    public async Task Aggregates_all_pages_until_hasMore_is_false()
    {
        // 3 pages: the stub reports HasMore=true until the roster is drained.
        var rows = Enumerable.Range(0, 450)
            .Select(i => Row($"user-{i:D4}", $"U{i}", active: "customer", available: new[] { "customer" }))
            .ToArray();
        var um = new StubUmClient(rows);

        var resp = await MakeFactory(um).CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("users").GetArrayLength().Should().Be(450);
        um.PagesServed.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Flag_off_openMode_false_returns_404()
    {
        var um = new StubUmClient(new[] { Row("x", "X") });
        var resp = await MakeFactory(um, openMode: false).CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        um.PagesServed.Should().Be(0); // never even hit UM
    }

    [Fact]
    public async Task DemoUsers_disabled_returns_404_even_when_openMode_true()
    {
        var um = new StubUmClient(new[] { Row("x", "X") });
        var resp = await MakeFactory(um, openMode: true, demoUsersEnabled: false)
            .CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Um_failure_returns_502()
    {
        var um = new ThrowingUmClient();
        var resp = await MakeFactory(um).CreateClient().GetAsync(Path);
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static UserProfileResponse Row(string userId, string? username = null,
        string? active = null, IEnumerable<string>? available = null)
        => new()
        {
            UserId = userId,
            Username = username,
            Active_role = active,
            Available_roles = available?.ToList(),
        };

    /// <summary>Stub over the generated UM client: pages the supplied roster through
    /// AllAsync exactly as UM's <c>GET /api/User/all</c> would (skip/limit + hasMore).</summary>
    private sealed class StubUmClient : UmClient
    {
        private readonly IReadOnlyList<UserProfileResponse> _all;
        public int PagesServed { get; private set; }

        public StubUmClient(IReadOnlyList<UserProfileResponse> all)
            : base("http://localhost", new HttpClient()) => _all = all;

        public override Task<GetAllUsersResponse> AllAsync(
            int? skip, int? limit, bool? onActive, System.Threading.CancellationToken ct)
        {
            PagesServed++;
            var s = skip ?? 0;
            var l = limit ?? _all.Count;
            var page = _all.Skip(s).Take(l).ToList();
            return Task.FromResult(new GetAllUsersResponse
            {
                Users = page,
                TotalCount = _all.Count,
                Skip = s,
                Limit = l,
                HasMore = s + page.Count < _all.Count,
            });
        }
    }

    private sealed class ThrowingUmClient : UmClient
    {
        public ThrowingUmClient() : base("http://localhost", new HttpClient()) { }

        public override Task<GetAllUsersResponse> AllAsync(
            int? skip, int? limit, bool? onActive, System.Threading.CancellationToken ct)
            => throw new JeebGateway.service.ServiceUserManagement.ApiException(
                "UM down", 503, "err", new Dictionary<string, IEnumerable<string>>(), null);
    }
}
