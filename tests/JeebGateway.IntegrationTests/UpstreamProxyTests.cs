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
        body!.Items.Should().HaveCount(3);
        body.Items.Select(t => t.Id).Should().BeEquivalentTo(new[] { "urgent", "same-day", "scheduled" });
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

        captured.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/tiers");
    }

    // -----------------------------------------------------------------
    // Matching: courier /matching/run was RELOCATED to delivery-service.
    // The gateway always delegates via IDeliveryServiceClient (no in-memory
    // engine, no feature flag). That delegation + the snake_case wire contract
    // are covered by MatchingEndpointTests + MatchingRunContractTests, so there
    // is no flag-gated matching case here anymore.
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // Location update — S06 presence wire.
    //
    // The legacy FeatureFlags:UseUpstream:Geolocation branch was REMOVED:
    // both old branches were broken (the geolocation client targeted a
    // /jeeb/jeebers/{id}/location/update route that does not exist). The GPS
    // ingest now UNCONDITIONALLY (a) records the batch to the in-memory store
    // (backs the SSE tracking read) and (b) forwards the device-latest fix to
    // delivery-service as a heartbeat — the SAME presence store the matching run
    // reads for freshness (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8). There is no
    // geolocation flag on this path anymore. The full heartbeat contract
    // (route, body, 404-tolerance) is locked in PresenceThinWireTests.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Location_Update_Records_InMemory_And_Heartbeats_Delivery_Presence()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(new
            {
                jeeber_id = "jeeber-1",
                online = true,
                lat = 31.95,
                lng = 35.92,
                updated_at = DateTimeOffset.UnixEpoch
            });
        });

        using var factory = NewFactory(
            configureServices: services =>
            {
                ReplaceTypedClient<IDeliveryServiceClient, DeliveryServiceClient>(
                    services, stub, "http://upstream-delivery.test");
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
        // The gateway response shape is unchanged — accepted/rejected come from
        // the in-memory store, NOT from any upstream body.
        body!.Accepted.Should().Be(1);
        body.Rejected.Should().Be(0);

        // The latest fix was forwarded to the canonical delivery-service presence
        // heartbeat route (jeeber id in the path).
        captured.Single().RequestUri!.AbsolutePath
            .Should().Be("/api/v1/jeebers/jeeber-1/heartbeat");
    }

    // NOTE: the legacy multipart KYC submit (old in-gateway KycController over the
    // auth-service proxy / in-memory pipeline) was DELETED in S03 / ADR-0004 — the
    // gateway now holds ZERO KYC state and the KYC surface is the thin JSON BFF
    // over the owning kyc-service (see Kyc/KycSubmissionBffEndpointTests). The two
    // obsolete tests that exercised that removed path were removed with it.

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
        var impl = (TImpl)Activator.CreateInstance(typeof(TImpl), new object?[] { http, null })!;
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
