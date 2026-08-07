using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Push;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServicePushNotification;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// feat/tier-unify-names — the tier-taxonomy unification suite.
///
/// <list type="bullet">
///   <item><description>The legacy→catalog alias table (<see cref="LegacyTierCodes"/>):
///     flash→urgent, express→urgent, standard→same-day, on_the_way→same-day,
///     eco→scheduled; unknown ids pass through untouched.</description></item>
///   <item><description><see cref="NewRequestPushNotifier"/> resolves a human display
///     name for BOTH catalog ids and legacy-mapped codes (the original defect: a legacy
///     code never resolved, so push bodies silently lost the tier suffix). Unresolvable
///     ids still drop the suffix (behavior kept).</description></item>
///   <item><description><c>POST /v1/requests</c> (JSON V1 path) now VALIDATES a supplied
///     tierId against the unified catalog: unknown → 404 with the machine-readable
///     <c>tier-not-found</c> type URI (same envelope as the legacy create surfaces);
///     catalog ids and legacy codes are both accepted; a tier-less create stays
///     allowed.</description></item>
/// </list>
/// </summary>
public class TierUnificationTests
{
    // ---------------------------------------------------------------------
    // LegacyTierCodes — the alias table itself.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("flash", "urgent")]
    [InlineData("express", "urgent")]
    [InlineData("standard", "same-day")]
    [InlineData("on_the_way", "same-day")]
    [InlineData("eco", "scheduled")]
    [InlineData("FLASH", "urgent")]        // case-insensitive, like the catalog store
    [InlineData(" eco ", "scheduled")]     // trimmed
    public void LegacyCode_MapsToItsCatalogEquivalent(string legacy, string expectedCatalogId)
    {
        LegacyTierCodes.TryMapToCatalogId(legacy, out var catalogId).Should().BeTrue();
        catalogId.Should().Be(expectedCatalogId);
        LegacyTierCodes.Canonicalize(legacy).Should().Be(expectedCatalogId);
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("same-day")]
    [InlineData("scheduled")]
    [InlineData("some-admin-added-tier")]
    public void NonLegacyId_PassesThroughCanonicalizeUntouched(string id)
    {
        LegacyTierCodes.TryMapToCatalogId(id, out _).Should().BeFalse();
        LegacyTierCodes.Canonicalize(id).Should().Be(id);
    }

    [Fact]
    public async Task EveryLegacyAliasTarget_ExistsInTheSeededCatalog()
    {
        // Guards the alias table against catalog-seed drift: each mapped target must
        // be a real seeded catalog row, or legacy clients would silently start 400-ing.
        var catalog = new InMemoryTiersStore();
        foreach (var legacy in new[] { "flash", "express", "standard", "on_the_way", "eco" })
        {
            LegacyTierCodes.TryMapToCatalogId(legacy, out var catalogId).Should().BeTrue();
            (await catalog.GetAsync(catalogId, CancellationToken.None))
                .Should().NotBeNull($"legacy '{legacy}' maps to '{catalogId}', which must exist in the seed");
        }
    }

    // ---------------------------------------------------------------------
    // NewRequestPushNotifier — display names resolve for catalog AND legacy ids.
    // ---------------------------------------------------------------------

    [Theory]
    // Catalog ids resolve directly.
    [InlineData("urgent", "Urgent")]
    [InlineData("same-day", "Same-Day")]
    [InlineData("scheduled", "Scheduled")]
    // Legacy-mapped codes resolve to their aliased catalog row's display name.
    [InlineData("flash", "Urgent")]
    [InlineData("express", "Urgent")]
    [InlineData("standard", "Same-Day")]
    [InlineData("on_the_way", "Same-Day")]
    [InlineData("eco", "Scheduled")]
    public async Task PushBody_CarriesDisplayName_ForCatalogAndLegacyTierIds(
        string tierId, string expectedDisplayName)
    {
        // P1: the push is now a per-user fan-out over the jeeber_availability roster, so the
        // body is read off the per-user rail. Tier display resolution itself is untouched.
        var push = new RecordingPushClient();
        var notifier = NewFanoutNotifier(push);

        await notifier.FanOutAsync(
            new NewRequestNotification("req-1", tierId, "Deliver a parcel", "customer-1", null, null),
            CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.UserSends.Single().Payload;
        ((string)payload["body"]!).Should().EndWith($" • {expectedDisplayName}",
            $"tier id '{tierId}' must resolve to the '{expectedDisplayName}' display name");
        // The RAW id (machine field) is carried untranslated — display resolution
        // never rewrites the client-facing filter field.
        payload["tierId"].Should().Be(tierId);
    }

    [Fact]
    public async Task PushBody_StillDropsSuffix_ForUnknownTierId()
    {
        // Behavior kept from the pre-unification notifier: an id that resolves in
        // NEITHER taxonomy drops the suffix (a raw id/UUID is never shown).
        var push = new RecordingPushClient();
        var notifier = NewFanoutNotifier(push);

        await notifier.FanOutAsync(
            new NewRequestNotification(
                "req-1", "definitely-not-a-tier", "Deliver a parcel", "customer-1", null, null),
            CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.UserSends.Single().Payload;
        ((string)payload["body"]!).Should().Be("Deliver a parcel");
    }

    /// <summary>
    /// P1 notifier over one online jeeber, so a single per-user send carries the payload
    /// whose tier body/id these theories assert.
    /// </summary>
    private static NewRequestPushNotifier NewFanoutNotifier(RecordingPushClient push)
        => new(
            push,
            new InMemoryTiersStore(),
            NullLogger<NewRequestPushNotifier>.Instance,
            new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("jeeberA") } },
            new FakeUsersStore(),
            new RecordingFanoutQueue(),
            Options.Create(new NewRequestFanoutOptions()),
            TimeProvider.System);

    // ---------------------------------------------------------------------
    // POST /v1/requests (JSON V1 path) — create-time tier validation, e2e.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("urgent")]      // catalog id
    [InlineData("flash")]       // legacy code (aliased to urgent)
    [InlineData("standard")]    // legacy default (aliased to same-day)
    public async Task V1Create_Accepts_CatalogAndLegacyTierIds(string tierId)
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Pick up keys", tierId));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task V1Create_UnknownTierId_Returns404TierNotFound_AndPublishesNothing()
    {
        var push = new RecordingTopicPushClient();
        var queue = new RecordingFanoutQueue();
        using var factory = NewFactory(push, queue);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", "platinum_super_fast"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/tier-not-found",
            "the reject must carry the same machine-readable code as the legacy create surfaces");
        problem.Detail.Should().Contain("platinum_super_fast");

        // A rejected create never reaches the push hook and persists no row.
        push.Sends.Should().BeEmpty();
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task V1Create_TierlessCreate_StaysAllowed()
    {
        // tierId remains OPTIONAL on the V1 surface — only a present-but-unknown
        // id is rejected. (A tier-less row skips the delivery-service seed.)
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", new
        {
            description = "No tier chosen yet",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task V1Create_WithLegacyTierId_PushBodyCarriesCatalogDisplayName()
    {
        // End-to-end proof of the original defect fix: a create with a LEGACY code
        // flows through to the jeebers push with a resolved display name (previously
        // the suffix was silently dropped because the code never hit a catalog row).
        // P1: the create now ENQUEUES; the recorded job is then driven through the app's
        // OWN notifier (real tier catalog, real DI) so the assertion stays end-to-end
        // without racing the hosted processor.
        var push = new RecordingPushClient();
        var queue = new RecordingFanoutQueue();
        var availability = new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("jeeberA") } };
        using var factory = NewFactory(push, queue, availability);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Deliver documents", "flash"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        queue.Jobs.Should().ContainSingle();

        using (var scope = factory.Services.CreateScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<INewRequestPushNotifier>();
            await notifier.FanOutAsync(queue.Jobs.Single(), CancellationToken.None);
        }

        push.UserSends.Should().ContainSingle();
        var payload = (IDictionary<string, object?>)push.UserSends.Single().Payload;
        ((string)payload["body"]!).Should().Contain("Urgent",
            "the legacy 'flash' code aliases to the 'urgent' catalog row");
        payload["tierId"].Should().Be("flash", "the raw machine field is never rewritten");
    }

    // ---------------------------------------------------------------------
    // POST /v1/requests — create-time validation is CONSISTENT with the READ
    // path when delivery-service is the authoritative tier source
    // (FeatureFlags:UseUpstream:Delivery = true). Regression guard for the P0:
    // the create-time probe used to consult ONLY the gateway-local slug catalog
    // and 400'd every upstream UUIDv5 tier id the mobile app faithfully submits,
    // blocking ALL request creation on the live (Delivery-upstream-on) box.
    // ---------------------------------------------------------------------

    // The live delivery-service Standard tier id (UUIDv5), exactly as
    // GET /api/v1/tiers returns it and the mobile tier-picker submits it.
    private const string UpstreamStandardTierId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";
    private const string UpstreamFlashTierId = "1a2b3c4d-5e6f-5a1b-8c2d-3e4f5a6b7c8d";
    private const string UpstreamExpressTierId = "9f1c0e6b-1b2a-5c3d-8e4f-0a1b2c3d4e5f";

    [Fact]
    public async Task V1Create_DeliveryUpstreamOn_AcceptsUpstreamTierId_Returns201()
    {
        // THE P0 regression test. With Delivery upstream on, the SAME id the read
        // path (GET /v1/tiers) returns must VALIDATE at create time — no more 400.
        var push = new RecordingTopicPushClient();
        using var factory = NewUpstreamDeliveryFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", UpstreamStandardTierId));

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "an upstream tier id the tier-picker rendered from must not 400 at create time");
    }

    [Theory]
    [InlineData(UpstreamStandardTierId, UpstreamStandardTierId)]
    [InlineData("flash", UpstreamFlashTierId)]
    [InlineData("express", UpstreamExpressTierId)]
    [InlineData("standard", UpstreamStandardTierId)]
    [InlineData("urgent", UpstreamFlashTierId)]
    [InlineData("same-day", UpstreamStandardTierId)]
    public async Task V1Create_DeliveryUpstreamOn_PersistsAndForwardsAuthoritativeTierId(
        string submittedTierId, string expectedTierId)
    {
        var push = new RecordingTopicPushClient();
        var upstream = new UpstreamTiersStubHandler();
        using var factory = NewUpstreamDeliveryFactory(push, upstream);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", submittedTierId));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<JeebGateway.Requests.DeliveryRequestDto>();
        created!.TierId.Should().Be(expectedTierId);

        var read = await client.GetFromJsonAsync<JeebGateway.Requests.DeliveryRequestDto>(
            $"/v1/requests/{created.Id}");
        read!.TierId.Should().Be(expectedTierId, "the persisted request must carry the delivery-resolvable id");

        upstream.DeliveryCreates.Should().ContainSingle();
        using var payload = JsonDocument.Parse(upstream.DeliveryCreates.Single());
        payload.RootElement.GetProperty("tier_id").GetString().Should().Be(expectedTierId);
    }

    [Theory]
    [InlineData("urgent", UpstreamFlashTierId)]
    [InlineData("same-day", UpstreamStandardTierId)]
    public async Task LegacyCreate_DeliveryUpstreamOn_PersistsAuthoritativeTierId(
        string submittedTierId, string expectedTierId)
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewUpstreamDeliveryFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/requests", ValidPayload("Legacy create", submittedTierId));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<JeebGateway.Requests.DeliveryRequestDto>();
        created!.TierId.Should().Be(expectedTierId);
    }

    [Fact]
    public async Task V1Create_DeliveryUpstreamOn_UnknownTierId_Returns404TierNotFound()
    {
        // A genuinely-unknown id is still rejected — with the EXACT same
        // ProblemDetails envelope (tier-not-found type URI) as before the fix.
        var push = new RecordingTopicPushClient();
        using var factory = NewUpstreamDeliveryFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", "00000000-0000-0000-0000-000000000000"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/tier-not-found",
            "an unknown id under the upstream branch keeps the same machine-readable code");
        problem.Detail.Should().Contain("00000000-0000-0000-0000-000000000000");
        push.Sends.Should().BeEmpty("a rejected create never reaches the push hook");
    }

    [Fact]
    public async Task V1Create_DeliveryUpstreamOff_UpstreamOnlyTierId_IsRejected()
    {
        // Symmetric guard on the OFF branch: with Delivery upstream off the probe
        // consults ONLY the gateway-local slug catalog, so an id that exists ONLY
        // upstream (a UUIDv5) is correctly rejected. Proves the ON branch is a real
        // behavioural fork, not a no-op that would accept anything.
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push); // default config => Delivery upstream OFF
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", UpstreamStandardTierId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/tier-not-found");
    }

    // ---------------------------------------------------------------------
    // helpers (same recorder/factory pattern as NewRequestPushNotifierTests)
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        ServicePushNotificationClient push,
        INewRequestFanoutQueue? queue = null,
        IAvailabilityStore? availability = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);

                    // P1: swapping in the recorder queue makes the create→fan-out hand-off
                    // deterministic (its reader never yields, so the hosted processor idles).
                    if (queue is not null)
                    {
                        services.RemoveAll<INewRequestFanoutQueue>();
                        services.AddSingleton(queue);
                    }

                    if (availability is not null)
                    {
                        services.RemoveAll<IAvailabilityStore>();
                        services.AddSingleton(availability);
                    }
                });
            });

    // Delivery-upstream-ON factory: flips FeatureFlags:UseUpstream:Delivery on (via
    // UseSetting, like S09HandoverIdempotentReverifyTests) and wires the REAL
    // DeliveryServiceClient over a stub HttpMessageHandler that serves the
    // delivery-service tier catalog at GET /api/v1/tiers — the SAME call
    // JeebTiersController.List uses — plus a benign 201 for the best-effort
    // POST /api/v1/deliveries row seed. This drives the whole fixed path end-to-end:
    // flag -> CatalogBackedTiersStore -> IDeliveryServiceClient.ListTiersAsync -> id match.
    private static WebApplicationFactory<Program> NewUpstreamDeliveryFactory(
        RecordingTopicPushClient push,
        UpstreamTiersStubHandler? upstream = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);

                    // Replace the production typed delivery client with the REAL client
                    // over a canned-catalog handler (mirrors UpstreamProxyTests).
                    services.RemoveAll<IDeliveryServiceClient>();
                    var http = new HttpClient(upstream ?? new UpstreamTiersStubHandler())
                    {
                        BaseAddress = new Uri("http://upstream-delivery.test/")
                    };
                    services.AddSingleton<IDeliveryServiceClient>(new DeliveryServiceClient(http));
                });
            });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    private static object ValidPayload(string description, string tierId) => new
    {
        description,
        tierId,
        pickupLocation = new { lat = 33.88, lng = 35.50 },
        dropoffLocation = new { lat = 33.89, lng = 35.51 },
    };

    private sealed record SendRecord(string Topic, object Payload);

    private sealed class RecordingTopicPushClient : ServicePushNotificationClient
    {
        public RecordingTopicPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();

        public override Task<SentPayloadResponse> Send_notification_to_topicAsync(
            string topicName, SentPayloadToTopicRequest body, CancellationToken cancellationToken)
        {
            Sends.Enqueue(new SendRecord(topicName, body.Payload));
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// Serves the delivery-service tier catalog (UUIDv5 ids, exactly like the live
    /// upstream) at <c>GET /api/v1/tiers</c> and a benign <c>201</c> for the
    /// best-effort <c>POST /api/v1/deliveries</c> row seed. Any other request gets a
    /// harmless 200 — the create path under test touches only these two routes.
    /// </summary>
    private sealed class UpstreamTiersStubHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public ConcurrentQueue<string> DeliveryCreates { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get
                && path.EndsWith("/api/v1/tiers", StringComparison.Ordinal))
            {
                var tiers = new[]
                {
                    new DeliveryTierDto
                    {
                        Id = UpstreamFlashTierId, Name = "Flash", SlaHours = 1,
                        RadiusKm = 8.0, CommissionRate = 0.10, PriceHint = "Fastest dispatch",
                        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                    new DeliveryTierDto
                    {
                        Id = UpstreamStandardTierId, Name = "Standard", SlaHours = 24,
                        RadiusKm = 5.0, CommissionRate = 0.10, PriceHint = "Standard rate",
                        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                    new DeliveryTierDto
                    {
                        Id = UpstreamExpressTierId, Name = "Express", SlaHours = 4,
                        RadiusKm = 8.0, CommissionRate = 0.10, PriceHint = "Faster dispatch",
                        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                };
                return Ok(JsonSerializer.Serialize(tiers, Json));
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/api/v1/deliveries", StringComparison.Ordinal))
            {
                DeliveryCreates.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        """{"delivery_id":"seeded","status":"Ordered"}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            return Ok("{}");
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
