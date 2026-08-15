using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Services.Generated.GeolocationService;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Geolocation;

/// <summary>
/// Regression for the W3-18 hardware finding: geolocation-service refusing the
/// gateway's credential (401) escaped <see cref="GeoServiceLocationStore"/> as the
/// generated <see cref="ApiException"/>, which is NOT an
/// <c>HttpRequestException</c>, so <c>UpstreamExceptionHandler</c> fell through to
/// its default arm and a courier mid-delivery got an opaque 500.
///
/// The handled contract asserted here: 503 + the geolocation-unavailable problem
/// type + <c>Retry-After</c>. Never 200 (O10: a silent success is worse than an
/// honest error) and never 401/403 (that is the gateway's credential failing, not
/// the courier's — echoing it would strand a courier at a re-login mid-delivery).
/// </summary>
public sealed class LocationUpstreamAuthFailureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static GeoServiceLocationStore StoreOver(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://geo.test/") };
        return new GeoServiceLocationStore(
            new GeolocationServiceClient(http),
            new StaticTrackingOptions(new TrackingOptions()),
            TimeProvider.System,
            NullLogger<GeoServiceLocationStore>.Instance);
    }

    /// <summary>Factory with the REAL upstream-backed store over a stubbed upstream.</summary>
    private static WebApplicationFactory<Program> FactoryOver(HttpMessageHandler upstream) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Geolocation", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILocationStore>();
                services.AddSingleton<ILocationStore>(StoreOver(upstream));
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            });
        });

    private static HttpMessageHandler Refusing(HttpStatusCode status) =>
        new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                """{"detail":"Not authenticated"}""", Encoding.UTF8, "application/json")
        });

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Update_UpstreamRefusesCredential_Returns_503_Not_500(HttpStatusCode upstreamStatus)
    {
        using var factory = FactoryOver(Refusing(upstreamStatus));

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.7100, lng = 46.6700, accuracy = 10.0, timestamp = DateTimeOffset.UtcNow },
            }
        });

        // RED before the fix: this is 500 InternalServerError.
        resp.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "an upstream credential refusal is a handled dependency failure, not a gateway crash");

        // Never the courier's own auth codes: the device must not treat its
        // session as expired because OUR credential is wrong.
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        problem!.Type.Should().Be(LocationUpstreamUnavailableException.ProblemType);
        problem.Status.Should().Be(503);

        // Bounded backoff instead of a hot retry loop on a high-frequency stream.
        resp.Headers.RetryAfter.Should().NotBeNull("the client must back off, not hot-loop");
    }

    [Fact]
    public async Task Update_UpstreamRefuses_Never_Reports_Success()
    {
        using var factory = FactoryOver(Refusing(HttpStatusCode.Unauthorized));

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.71, lng = 46.67, timestamp = DateTimeOffset.UtcNow },
            }
        });

        // O10: papering the 401 over as an accepted=0 200 is the forbidden outcome.
        resp.IsSuccessStatusCode.Should().BeFalse(
            "a dropped GPS batch must never be reported to the device as stored");
    }

    [Fact]
    public async Task RecordAsync_Translates_Upstream_401_Into_The_Domain_Failure()
    {
        var store = StoreOver(Refusing(HttpStatusCode.Unauthorized));

        var act = async () => await store.RecordAsync("jeeber-1", new[]
        {
            new GpsPointDto { Lat = 24.71, Lng = 46.67, Timestamp = DateTimeOffset.UtcNow },
        });

        // RED before the fix: the raw generated ApiException escapes the seam.
        (await act.Should().ThrowAsync<LocationUpstreamUnavailableException>())
            .Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task GetLatestAsync_Translates_Upstream_401_Into_The_Domain_Failure()
    {
        var store = StoreOver(Refusing(HttpStatusCode.Unauthorized));

        var act = async () => await store.GetLatestAsync("jeeber-1");

        (await act.Should().ThrowAsync<LocationUpstreamUnavailableException>())
            .Which.StatusCode.Should().Be(401);
    }

    /// <summary>A 404 on read stays "no fix on record" — the fix must not swallow it.</summary>
    [Fact]
    public async Task GetLatestAsync_404_Still_Maps_To_Null_Not_A_Failure()
    {
        var store = StoreOver(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await store.GetLatestAsync("jeeber-1")).Should().BeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }

    private sealed class StaticTrackingOptions : IOptionsMonitor<TrackingOptions>
    {
        public StaticTrackingOptions(TrackingOptions value) => CurrentValue = value;
        public TrackingOptions CurrentValue { get; }
        public TrackingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TrackingOptions, string?> listener) => null;
    }
}
