using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests for the ADDITIVE, ENV-GATED developer endpoints under <c>/dev/*</c>
/// (<see cref="JeebGateway.Controllers.DevController"/>), per
/// <c>SEED-SESSIONS-CONTRACT.md §1</c>.
///
/// Two contracts are asserted:
///   * <b>flag-off → 404</b> on EVERY dev route (the
///     <see cref="JeebGateway.Security.DevOnlyAttribute"/> gate). This is the
///     production-safety guarantee — the routes are indistinguishable from
///     "no such endpoint" while <c>Features:DevEndpoints:Enabled</c> is false
///     (which is the committed value in every environment).
///   * <b>flag-on</b> → <c>POST /dev/seed/user</c> calls the existing typed
///     <see cref="ServiceUserManagementClient"/> with the mapped
///     <see cref="RegisterUserRequest"/> and returns the upstream
///     <c>userId</c>; the inspect routes proxy the same client.
///
/// The UM client is replaced with one whose <see cref="HttpClient"/> is backed
/// by a stub handler (the same pattern as <c>UserPreferencesEndpointTests</c>),
/// so no live user-management is required.
/// </summary>
public class DevEndpointsTests
{
    // -----------------------------------------------------------------
    // flag OFF -> 404 on every dev route (production-safety guarantee)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/dev/seed/user")]
    [InlineData("GET", "/dev/data/users")]
    [InlineData("GET", "/dev/data/users?runId=7f3a1c")]
    [InlineData("GET", "/dev/data/user/abc-123")]
    public async Task DevRoutes_FlagOff_Return404(string method, string path)
    {
        // No stub needed: the gate short-circuits before any upstream call.
        using var factory = NewFactory(enabled: false, upstreamHandler: ThrowingHandler());
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = JsonBody("""
                { "role": "client", "phone": "+96139120001", "displayName": "Sami" }
                """);
        }

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "every /dev/* route must behave as if it does not exist while the flag is off");
    }

    // -----------------------------------------------------------------
    // flag ON -> POST /dev/seed/user maps to UM RegisterUserRequest and
    // returns the upstream userId.
    // -----------------------------------------------------------------

    [Fact]
    public async Task SeedUser_FlagOn_CallsUserManagement_WithMappedRequest_AndReturnsUserId()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            // UM RegisterUserResponse — the canonical id the gateway echoes.
            return JsonResponse("""
                {
                  "userId": "f1c2-real-um-id",
                  "username": "sami_run7f3a",
                  "email": "seed-7f3a-sami@jeeb.test",
                  "status": "created",
                  "createdDate": "2026-06-05T09:00:00Z"
                }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            {
              "role": "Client",
              "phone": "+96139120001",
              "displayName": "Sami (run 7f3a)",
              "runId": "7f3a1c",
              "tags": ["S02", "sami"]
            }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<SeedUserResponseDto>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("f1c2-real-um-id");
        body.Role.Should().Be("client", "role is normalized to lowercase");
        body.Phone.Should().Be("+96139120001", "phone is echoed");
        body.RunId.Should().Be("7f3a1c");
        body.Tags.Should().Contain("S02").And.Contain("sami");
        body.Status.Should().Be("created");

        // The upstream UM register endpoint was hit exactly once with a POST.
        var sent = captured.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().EndWith("/api/User/register");

        // The mapped RegisterUserRequest carries a derived username/email and a
        // password == confirmPassword (the gateway generated a strong random pw),
        // and NEVER reflects the raw phone as a UM field name we did not map.
        var json = captured.LastBody;
        json.Should().Contain("\"email\":");
        json.Should().Contain("\"username\":");
        json.Should().Contain("\"password\":");
        json.Should().Contain("\"confirmPassword\":");
        json.Should().NotContain("\"phone\"", "UM has no phone field; the gateway must not invent one");
        json.Should().NotContain("\"role\"", "UM has no role field; role is seed metadata only");
    }

    [Fact]
    public async Task SeedUser_FlagOn_NeverReturnsPassword()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""
            { "userId": "id-1", "username": "u1", "email": "seed-x@jeeb.test", "status": "created" }
            """));

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "jeeber", "phone": "+96139120002", "displayName": "Lina" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("password",
            "the dev seed response must never carry a password");
    }

    [Fact]
    public async Task SeedUser_FlagOn_MissingRequiredFields_Returns400()
    {
        using var factory = NewFactory(enabled: true, upstreamHandler: ThrowingHandler());
        var client = factory.CreateClient();

        // Missing displayName.
        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "client", "phone": "+96139120003" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SeedUser_FlagOn_UpstreamConflict_IsSurfaced()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("username taken", Encoding.UTF8, "text/plain"),
            });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "client", "phone": "+96139120004", "displayName": "Dup" }
            """));

        // The gateway surfaces the upstream 4xx (not a 200).
        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // flag ON -> GET /dev/data/users proxies AllAsync and shapes the view
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUsers_FlagOn_ProxiesUserManagement_AndShapesView()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "users": [
                    { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test", "createdDate": "2026-06-05T09:00:00Z" }
                  ],
                  "totalCount": 1, "skip": 0, "limit": 50, "hasMore": false
                }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/users?runId=7f3a");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UsersResponseDto>();
        body.Should().NotBeNull();
        body!.Source.Should().Be("user-management");
        body.RunIdFilter.Should().Be("7f3a");
        body.Count.Should().Be(1);
        body.Users.Should().ContainSingle(u => u.UserId == "id-1");

        captured.Single().Method.Should().Be(HttpMethod.Get);
        captured.Single().RequestUri!.AbsolutePath.Should().EndWith("/api/User/all");
    }

    [Fact]
    public async Task GetUsers_FlagOn_RunIdFilter_ExcludesNonMatching()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "users": [
                { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test" },
                { "userId": "id-2", "username": "other_runZZZZ", "email": "seed-zzzz-other@jeeb.test" }
              ],
              "totalCount": 2, "skip": 0, "limit": 50, "hasMore": false
            }
            """));

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/users?runId=7f3a");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UsersResponseDto>();
        body!.Count.Should().Be(1, "only the user whose handle/email carries the run tag matches");
        body.Users.Single().UserId.Should().Be("id-1");
    }

    // -----------------------------------------------------------------
    // flag ON -> GET /dev/data/user/{id} proxies ProfileAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUser_FlagOn_ProxiesProfile_AndShapesView()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test", "createdDate": "2026-06-05T09:00:00Z" }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/user/id-1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DevUserViewDto>();
        body!.UserId.Should().Be("id-1");
        body.Username.Should().Be("sami_run7f3a");

        captured.Single().Method.Should().Be(HttpMethod.Get);
        captured.Single().RequestUri!.AbsolutePath.Should().Contain("/api/User/");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(bool enabled, HttpMessageHandler upstreamHandler)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Features:DevEndpoints:Enabled", enabled ? "true" : "false");

                builder.ConfigureTestServices(services =>
                {
                    // Replace the scoped UM client with one whose HttpClient is
                    // backed by the stub handler.
                    services.RemoveAll<ServiceUserManagementClient>();
                    services.AddScoped(_ =>
                    {
                        var http = new HttpClient(upstreamHandler)
                        {
                            BaseAddress = new Uri("http://um.test/"),
                        };
                        return new ServiceUserManagementClient("http://um.test/", http);
                    });
                });
            });
    }

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>A handler that fails the test if any upstream call is made.</summary>
    private static StubHttpMessageHandler ThrowingHandler()
        => new(_ => throw new InvalidOperationException(
            "upstream user-management must NOT be called when the dev flag is off or the request is invalid"));

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        private readonly List<string> _bodies = new();
        public void Add(HttpRequestMessage req, string body)
        {
            _items.Add(req);
            _bodies.Add(body);
        }
        public HttpRequestMessage Single() => _items.Single();
        public string LastBody => _bodies[^1];
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    // --- response DTOs (test-local; mirror DevController response shapes) ---

    private sealed class SeedUserResponseDto
    {
        public string? UserId { get; set; }
        public string? Role { get; set; }
        public string? Phone { get; set; }
        public string? DisplayName { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Status { get; set; }
        public string? RunId { get; set; }
        public string[]? Tags { get; set; }
    }

    private sealed class UsersResponseDto
    {
        public List<DevUserViewDto> Users { get; set; } = new();
        public int Count { get; set; }
        public string? Source { get; set; }
        public string? RunIdFilter { get; set; }
    }

    private sealed class DevUserViewDto
    {
        public string? UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Status { get; set; }
    }
}
