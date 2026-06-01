using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests for the user-preferences BFF surface (<c>/users/me/preferences</c>),
/// wired to the real <c>remote-user-preferences</c> service (host 10067).
///
/// Three layers, mirroring the repo's existing pattern:
///   1. WebApplicationFactory endpoint tests with a stubbed
///      <see cref="IUserPreferencesClient"/> backing HttpClient — happy + negative.
///   2. A literal-body JSON-seam contract test that drives the REAL
///      <see cref="UserPreferencesClient"/> against a fake handler returning the
///      exact upstream snake_case body, locking the wire mapping.
///   3. An OPT-IN real-wire test against the live upstream at :10067, run only
///      when JEEB_RUP_LIVE=1 so CI without VPN access stays green.
/// </summary>
public class UserPreferencesEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------
    // (1) Endpoint tests — happy path
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAll_With_Flag_On_Forwards_To_Upstream_And_Returns_Map()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            // GET /preferences/{user_id} -> Preferences map (snake_case values)
            return JsonResponse(new Dictionary<string, string>
            {
                ["theme"] = "dark",
                ["language"] = "ar",
            });
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "true" } },
            configureServices: services =>
                ReplaceTypedClient<IUserPreferencesClient, UserPreferencesClient>(
                    services, stub, "http://rup.test/"));

        var client = ClientWith(factory, "user-1");
        var resp = await client.GetAsync("/users/me/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body!.Should().ContainKey("theme").WhoseValue.Should().Be("dark");
        body.Should().ContainKey("language").WhoseValue.Should().Be("ar");

        // The caller's id (from X-User-Id) is the upstream path segment.
        captured.Single().RequestUri!.AbsolutePath.Should().Be("/preferences/user-1");
    }

    [Fact]
    public async Task GetOne_With_Flag_On_Returns_Value_From_Upstream_Envelope()
    {
        var stub = new StubHttpMessageHandler(_ =>
            JsonResponse(new { value = "dark" })); // PreferenceValue { value }

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "true" } },
            configureServices: services =>
                ReplaceTypedClient<IUserPreferencesClient, UserPreferencesClient>(
                    services, stub, "http://rup.test/"));

        var client = ClientWith(factory, "user-1");
        var resp = await client.GetAsync("/users/me/preferences/theme");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreferenceValueResponse>();
        body!.Value.Should().Be("dark");
    }

    [Fact]
    public async Task Set_With_Flag_On_Posts_To_Upstream_And_Echoes_Value()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "true" } },
            configureServices: services =>
                ReplaceTypedClient<IUserPreferencesClient, UserPreferencesClient>(
                    services, stub, "http://rup.test/"));

        var client = ClientWith(factory, "user-1");
        var resp = await client.PutAsJsonAsync(
            "/users/me/preferences/theme", new PreferenceValueResponse { Value = "light" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreferenceValueResponse>();
        body!.Value.Should().Be("light");

        var sent = captured.Single();
        sent.Method.Should().Be(HttpMethod.Post); // upstream uses POST for set-single
        sent.RequestUri!.AbsolutePath.Should().Be("/preferences/user-1/theme");
    }

    // -----------------------------------------------------------------
    // (1) Endpoint tests — negative paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAll_Without_Identity_Returns_401()
    {
        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "true" } });

        var client = factory.CreateClient(); // no X-User-Id, no JWT
        var resp = await client.GetAsync("/users/me/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAll_With_Flag_Off_Returns_503_Without_Calling_Upstream()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called when flag is off"));

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "false" } },
            configureServices: services =>
                ReplaceTypedClient<IUserPreferencesClient, UserPreferencesClient>(
                    services, stub, "http://rup.test/"));

        var client = ClientWith(factory, "user-1");
        var resp = await client.GetAsync("/users/me/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetOne_Maps_Upstream_404_To_404()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:RemoteUserPreferences", "true" } },
            configureServices: services =>
                ReplaceTypedClient<IUserPreferencesClient, UserPreferencesClient>(
                    services, stub, "http://rup.test/"));

        var client = ClientWith(factory, "user-1");
        var resp = await client.GetAsync("/users/me/preferences/missing-key");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // (2) JSON-seam contract test — REAL client against literal upstream body.
    // Locks the snake_case PreferenceValue envelope + the Preferences map.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Client_Binds_Literal_Upstream_PreferenceValue_Body()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "value": "dark" }""", Encoding.UTF8, "application/json")
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rup.test/") };
        var client = new UserPreferencesClient(http);

        var value = await client.GetAsync("user-1", "theme", CancellationToken.None);
        value.Should().Be("dark");
    }

    [Fact]
    public async Task Client_Binds_Literal_Upstream_Preferences_Map_Body()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "theme": "dark", "language": "ar" }""", Encoding.UTF8, "application/json")
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rup.test/") };
        var client = new UserPreferencesClient(http);

        var map = await client.GetAllAsync("user-1", CancellationToken.None);
        map.Should().ContainKey("theme").WhoseValue.Should().Be("dark");
        map.Should().ContainKey("language").WhoseValue.Should().Be("ar");
    }

    [Fact]
    public async Task Client_Returns_Null_On_Upstream_404()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rup.test/") };
        var client = new UserPreferencesClient(http);

        var value = await client.GetAsync("user-1", "missing", CancellationToken.None);
        value.Should().BeNull();
    }

    // -----------------------------------------------------------------
    // (3) OPT-IN real-wire test against the LIVE remote-user-preferences
    // service at 192.168.2.50:10067. Runs only when JEEB_RUP_LIVE=1 so CI
    // without VPN access stays green. Exercises the full round-trip:
    // POST set -> GET single -> GET all, against the service's real datastore.
    // -----------------------------------------------------------------

    [Fact]
    public async Task RealWire_RoundTrip_Against_Live_Upstream()
    {
        if (Environment.GetEnvironmentVariable("JEEB_RUP_LIVE") != "1")
        {
            return; // opt-in only; not run in CI
        }

        var baseUrl = Environment.GetEnvironmentVariable("JEEB_RUP_BASEURL")
                      ?? "http://192.168.2.50:10067/";
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var client = new UserPreferencesClient(http);

        var userId = $"jeeb-gateway-itest-{Guid.NewGuid():N}";
        var prefKey = "theme";
        var prefValue = "dark";

        await client.SetAsync(userId, prefKey, prefValue, CancellationToken.None);

        var single = await client.GetAsync(userId, prefKey, CancellationToken.None);
        single.Should().Be(prefValue);

        var all = await client.GetAllAsync(userId, CancellationToken.None);
        all.Should().ContainKey(prefKey).WhoseValue.Should().Be(prefValue);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        Dictionary<string, string?>? flags = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                if (flags is { Count: > 0 })
                {
                    builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(flags!));
                }
                if (configureServices is not null)
                {
                    builder.ConfigureTestServices(configureServices);
                }
            });
    }

    private static HttpClient ClientWith(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    private static void ReplaceTypedClient<TInterface, TImpl>(
        IServiceCollection services,
        HttpMessageHandler handler,
        string baseUrl)
        where TInterface : class
        where TImpl : class, TInterface
    {
        services.RemoveAll<TInterface>();
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        var impl = (TImpl)Activator.CreateInstance(typeof(TImpl), http)!;
        services.AddSingleton<TInterface>(impl);
    }

    private static HttpResponseMessage JsonResponse<T>(T payload, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        public void Add(HttpRequestMessage req) => _items.Add(req);
        public HttpRequestMessage Single() => _items.Single();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
