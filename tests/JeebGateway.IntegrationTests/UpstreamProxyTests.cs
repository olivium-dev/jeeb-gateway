using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Kyc;
using JeebGateway.Matching;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-migrate-gateway-proxies (PR-A). Verifies that each migrated
/// controller honours its <c>FeatureFlags:UseUpstream:*</c> flag:
/// flag=true → typed client is invoked and its payload is surfaced;
/// flag=false → the existing in-memory implementation is preserved.
///
/// Each typed client is registered as a singleton against a
/// <see cref="StubHttpMessageHandler"/> so tests assert both that the
/// upstream URL was hit AND that the controller returned the upstream
/// body — not just that the flag toggled some flag bit.
/// </summary>
public class UpstreamProxyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------
    // Tiers (delivery-service)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Tiers_With_Flag_Off_Returns_InMemory_Catalog()
    {
        using var factory = NewFactory(flags: new() { { "FeatureFlags:UseUpstream:Delivery", "false" } });
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/tiers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TiersListDto>();
        body!.Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task Tiers_With_Flag_On_Forwards_To_Delivery_Upstream()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(new[]
            {
                new DeliveryTierDto
                {
                    Id = "upstream-only", Name = "Upstream Only", SlaHours = 99,
                    RadiusKm = 1.0, CommissionRate = 0.1, PriceHint = "from-upstream",
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
                }
            });
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Delivery", "true" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IDeliveryServiceClient, DeliveryServiceClient>(
                    services, stub, "http://upstream-delivery.test");
            });

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/tiers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TiersListDto>();
        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be("upstream-only");

        captured.Single().RequestUri!.AbsolutePath.Should().Be("/jeeb/tiers");
    }

    // -----------------------------------------------------------------
    // Matching (matching-service)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Matching_With_Flag_Off_Uses_InMemory_Engine()
    {
        // The in-memory engine returns a real response (notifiedCount may be 0
        // when no Jeebers are seeded); the point is that the upstream stub is
        // not contacted.
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called when flag is off"));

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Matching", "false" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IMatchingServiceClient, MatchingServiceClient>(
                    services, stub, "http://upstream-matching.test");
            });

        var client = ClientWith(factory, "client-1", "customer");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = 31.95,
            pickupLng = 35.92,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        // In-memory dry-run path tags the request id with the "dryrun:" prefix.
        body!.RequestId.Should().StartWith("dryrun:");
    }

    [Fact]
    public async Task Matching_With_Flag_On_Forwards_To_Matching_Upstream()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(new MatchingRunResponse
            {
                RequestId = "upstream-rq-1",
                TierId = "urgent",
                RadiusKm = 5.0,
                NotifiedCount = 3,
                CandidateCount = 3,
                Candidates = Array.Empty<MatchedJeeberDto>(),
                ElapsedMs = 42
            });
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Matching", "true" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IMatchingServiceClient, MatchingServiceClient>(
                    services, stub, "http://upstream-matching.test");
            });

        var client = ClientWith(factory, "client-1", "customer");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = 31.95,
            pickupLng = 35.92,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.RequestId.Should().Be("upstream-rq-1");
        body.NotifiedCount.Should().Be(3);

        captured.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/jeeb/matching/run");
    }

    // -----------------------------------------------------------------
    // Location update (geolocation-service)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Location_Update_With_Flag_Off_Writes_To_InMemory_Store()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called when flag is off"));

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Geolocation", "false" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IGeolocationServiceClient, GeolocationServiceClient>(
                    services, stub, "http://upstream-geo.test");
            });

        var jeeber = ClientWith(factory, "jeeber-1", "driver");
        var resp = await jeeber.PostAsJsonAsync("/location/update", new
        {
            points = new[]
            {
                new { lat = 31.95, lng = 35.92, accuracy = 5.0, timestamp = DateTimeOffset.UtcNow }
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>();
        body!.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task Location_Update_With_Flag_On_Forwards_To_Geolocation_Upstream()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(new LocationUpdateResponse
            {
                Accepted = 7,
                Rejected = 1,
                Latest = new GpsPointDto
                {
                    Lat = 31.95, Lng = 35.92, Accuracy = 4.2,
                    Timestamp = DateTimeOffset.UnixEpoch
                }
            });
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Geolocation", "true" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IGeolocationServiceClient, GeolocationServiceClient>(
                    services, stub, "http://upstream-geo.test");
            });

        var jeeber = ClientWith(factory, "jeeber-1", "driver");
        var resp = await jeeber.PostAsJsonAsync("/location/update", new
        {
            points = new[]
            {
                new { lat = 31.95, lng = 35.92, accuracy = 5.0, timestamp = DateTimeOffset.UtcNow }
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>();
        body!.Accepted.Should().Be(7);
        body.Rejected.Should().Be(1);

        captured.Single().RequestUri!.AbsolutePath
            .Should().Be("/jeeb/jeebers/jeeber-1/location/update");
    }

    // -----------------------------------------------------------------
    // KYC submit (auth-service)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Kyc_Submit_With_Flag_On_Forwards_To_Auth_Upstream()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(new KycSubmissionResponse
            {
                Id = "upstream-kyc-1",
                UserId = "jeeber-1",
                Status = "pending_review",
                SubmittedAt = DateTimeOffset.UnixEpoch,
                VehicleType = "car",
                VehicleRegistration = "ABC-123",
                LivenessPassed = true,
                ResubmitSteps = Array.Empty<string>()
            }, HttpStatusCode.Accepted);
        });

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Auth", "true" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IAuthServiceClient, AuthServiceClient>(
                    services, stub, "http://upstream-auth.test");
            });

        var jeeber = ClientWith(factory, "jeeber-1", "driver");

        using var form = NewKycForm();
        var resp = await jeeber.PostAsync("/kyc/submit", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<KycSubmissionResponse>();
        body!.Id.Should().Be("upstream-kyc-1");

        var sent = captured.Single();
        sent.RequestUri!.AbsolutePath.Should().Be("/api/jeeb/kyc/submit");
        sent.Headers.GetValues("X-User-Id").Single().Should().Be("jeeber-1");
    }

    [Fact]
    public async Task Kyc_Submit_With_Flag_Off_Uses_InMemory_Pipeline()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called when flag is off"));

        using var factory = NewFactory(
            flags: new() { { "FeatureFlags:UseUpstream:Auth", "false" } },
            configureServices: services =>
            {
                ReplaceTypedClient<IAuthServiceClient, AuthServiceClient>(
                    services, stub, "http://upstream-auth.test");
            });

        var jeeber = ClientWith(factory, "jeeber-flag-off", "driver");

        using var form = NewKycForm();
        var resp = await jeeber.PostAsync("/kyc/submit", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<KycSubmissionResponse>();
        // The in-memory KYC service mints a fresh GUID-based id; we only
        // need to confirm we did NOT see the upstream-canned id.
        body!.Id.Should().NotBe("upstream-kyc-1");
        body.UserId.Should().Be("jeeber-flag-off");
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
                    builder.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(flags!);
                    });
                }
                if (configureServices is not null)
                {
                    builder.ConfigureTestServices(configureServices);
                }
            });
    }

    private static HttpClient ClientWith(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private static void ReplaceTypedClient<TInterface, TImpl>(
        IServiceCollection services,
        HttpMessageHandler handler,
        string baseUrl)
        where TInterface : class
        where TImpl : class, TInterface
    {
        // Strip the production typed-client registration so the test stub
        // takes its place. RemoveAll handles both the named-options and
        // typed registrations added by AddHttpClient.
        services.RemoveAll<TInterface>();

        var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        var impl = (TImpl)Activator.CreateInstance(typeof(TImpl), http)!;
        services.AddSingleton<TInterface>(impl);
    }

    private static HttpResponseMessage JsonResponse<T>(T payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        };
    }

    private static MultipartFormDataContent NewKycForm()
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("car"), "vehicleType" },
            { new StringContent("ABC-123"), "vehicleRegistration" }
        };
        AddPng(form, "idFront", "front.png");
        AddPng(form, "idBack", "back.png");
        AddPng(form, "selfie", "selfie.png");
        return form;
    }

    private static void AddPng(MultipartFormDataContent form, string field, string fileName)
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(content, field, fileName);
    }

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        public void Add(HttpRequestMessage req) => _items.Add(req);
        public HttpRequestMessage Single() => _items.Single();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed record TiersListDto(DeliveryTierDto[] Items);
}
