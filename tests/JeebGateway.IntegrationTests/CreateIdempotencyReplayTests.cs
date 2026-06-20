using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S05 A4 (JEB-45 AC6): replaying POST /requests with the SAME Idempotency-Key
/// returns the original create response byte-for-byte (same <c>$.id</c>), so a
/// double-tapped create is exactly one order.
///
/// The dedup is owned by the gateway-wide <see cref="IdempotencyMiddleware"/>
/// (R1 / JEB-1493), which is wired whenever jeeb-state-service is configured
/// (<c>JeebStateService:BaseUrl</c> set). This test mounts the middleware by
/// setting that base URL and replaces the state-service-backed
/// <see cref="IIdempotencyStore"/> with an in-process fake so the assertion does
/// not require a live state-service. Live-GREEN therefore additionally requires
/// the gateway env to carry <c>JeebStateService:BaseUrl</c> (a deploy concern).
/// </summary>
public sealed class CreateIdempotencyReplayTests : IClassFixture<CreateIdempotencyReplayTests.IdempotencyFactory>
{
    private readonly IdempotencyFactory _factory;

    public CreateIdempotencyReplayTests(IdempotencyFactory factory) => _factory = factory;

    public sealed class IdempotencyFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // Mount the gateway-wide idempotency middleware (gated on a configured
            // state-service) without needing the real service.
            builder.UseSetting("JeebStateService:BaseUrl", "http://localhost:10073");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IIdempotencyStore));
                services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            });
        }
    }

    [Fact]
    public async Task A4_Replay_With_Same_Key_Returns_Same_Id()
    {
        var client = ClientFor("s05-a4-replay");
        var key = Guid.NewGuid().ToString();

        var first = await Post(client, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstDto = await first.Content.ReadFromJsonAsync<CreatedDto>();

        var replay = await Post(client, key);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var replayDto = await replay.Content.ReadFromJsonAsync<CreatedDto>();

        replayDto!.Id.Should().Be(firstDto!.Id, "an Idempotency-Key replay must collapse onto the original order id");
        replay.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Different_Keys_Create_Distinct_Orders()
    {
        var client = ClientFor("s05-a4-distinct");

        var a = await Post(client, Guid.NewGuid().ToString());
        var b = await Post(client, Guid.NewGuid().ToString());

        var aDto = await a.Content.ReadFromJsonAsync<CreatedDto>();
        var bDto = await b.Content.ReadFromJsonAsync<CreatedDto>();
        aDto!.Id.Should().NotBe(bDto!.Id);
    }

    /// <summary>
    /// WS-08 hardening: a 201 replay must reproduce the original <c>Location</c>
    /// header so a create-then-locate client flow survives the idempotent retry.
    /// The persisted record carries only status + body (the state-service schema is
    /// a GATED contract), so the middleware reconstructs <c>Location</c> from the
    /// replayed body's <c>$.id</c>. Both the live create and the replay must point
    /// at the SAME <c>/requests/{id}</c> resource.
    /// </summary>
    [Fact]
    public async Task Replay_Of_A_201_Create_Reinstates_The_Location_Header()
    {
        var client = ClientFor("s05-a4-location");
        var key = Guid.NewGuid().ToString();

        var first = await Post(client, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstLocation = first.Headers.Location;
        firstLocation.Should().NotBeNull("the live create returns Location: /requests/{id}");

        var replay = await Post(client, key);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        replay.Headers.Location.Should().NotBeNull("the replay must not drop Location");
        replay.Headers.Location!.ToString()
            .Should().Be(firstLocation!.ToString(),
                "replay Location must point at the same resource as the original create");
    }

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/requests")
        {
            Content = JsonContent.Create(new
            {
                description = "I need two manakish and a bottle of water from the bakery",
                tierId = "flash",
                pickupLocation = new { lat = 33.88, lng = 35.50 },
                dropoffLocation = new { lat = 33.89, lng = 35.51 }
            })
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(req);
    }

    private sealed record CreatedDto(string Id, string Status);

    /// <summary>
    /// In-process stand-in for the state-service-backed idempotency store. Same
    /// atomic put-or-get semantics (ON CONFLICT DO NOTHING) without the network
    /// hop, so the middleware behaviour is exercised deterministically.
    /// </summary>
    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, IdempotencyOutcome> _rows = new();

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            var mine = new IdempotencyOutcome
            {
                Inserted = true,
                StatusCode = statusCode,
                ResponseBodyJson = responseBodyJson
            };
            var stored = _rows.GetOrAdd(key, mine);

            // Inserted is true only for the caller whose row is the one now stored.
            var inserted = ReferenceEquals(stored, mine);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = stored.StatusCode,
                ResponseBodyJson = stored.ResponseBodyJson
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
        {
            _rows.TryGetValue(key, out var row);
            return Task.FromResult(row is null
                ? null
                : new IdempotencyOutcome { Inserted = false, StatusCode = row.StatusCode, ResponseBodyJson = row.ResponseBodyJson });
        }
    }
}
