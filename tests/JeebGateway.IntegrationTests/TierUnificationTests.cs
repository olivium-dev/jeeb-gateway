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
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// feat/tier-unify-names — the tier-taxonomy unification suite.
///
/// <list type="bullet">
///   <item><description>The legacy→catalog alias table (<see cref="LegacyTierCodes"/>):
///     flash→urgent, express→urgent, standard→same-day, on_the_way→on-the-way,
///     eco→economy; unknown ids pass through untouched.</description></item>
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
    [InlineData("on_the_way", "on-the-way")]
    [InlineData("eco", "economy")]
    [InlineData("FLASH", "urgent")]        // case-insensitive, like the catalog store
    [InlineData(" eco ", "economy")]       // trimmed
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
    [InlineData("economy")]
    [InlineData("on-the-way")]
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
    [InlineData("economy", "Economy")]
    [InlineData("on-the-way", "On-the-Way")]
    // Legacy-mapped codes resolve to their aliased catalog row's display name.
    [InlineData("flash", "Urgent")]
    [InlineData("express", "Urgent")]
    [InlineData("standard", "Same-Day")]
    [InlineData("on_the_way", "On-the-Way")]
    [InlineData("eco", "Economy")]
    public async Task PushBody_CarriesDisplayName_ForCatalogAndLegacyTierIds(
        string tierId, string expectedDisplayName)
    {
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(
            push, new InMemoryTiersStore(), NullLogger<NewRequestPushNotifier>.Instance);

        await notifier.NotifyNewRequestAsync("req-1", tierId, "Deliver a parcel", CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.Sends.Single().Payload;
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
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(
            push, new InMemoryTiersStore(), NullLogger<NewRequestPushNotifier>.Instance);

        await notifier.NotifyNewRequestAsync(
            "req-1", "definitely-not-a-tier", "Deliver a parcel", CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.Sends.Single().Payload;
        ((string)payload["body"]!).Should().Be("Deliver a parcel");
    }

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
        using var factory = NewFactory(push);
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
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Deliver documents", "flash"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        push.Sends.Should().ContainSingle();
        var payload = (IDictionary<string, object?>)push.Sends.Single().Payload;
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

    private static WebApplicationFactory<Program> NewFactory(RecordingTopicPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);
                });
            });

    // Delivery-upstream-ON factory: flips FeatureFlags:UseUpstream:Delivery on (via
    // UseSetting, like S09HandoverIdempotentReverifyTests) and wires the REAL
    // DeliveryServiceClient over a stub HttpMessageHandler that serves the
    // delivery-service tier catalog at GET /api/v1/tiers — the SAME call
    // JeebTiersController.List uses — plus a benign 201 for the best-effort
    // POST /api/v1/deliveries row seed. This drives the whole fixed path end-to-end:
    // flag -> CatalogBackedTiersStore -> IDeliveryServiceClient.ListTiersAsync -> id match.
    private static WebApplicationFactory<Program> NewUpstreamDeliveryFactory(RecordingTopicPushClient push)
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
                    var http = new HttpClient(new UpstreamTiersStubHandler())
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

        protected override Task<HttpResponseMessage> SendAsync(
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
                        Id = UpstreamStandardTierId, Name = "Standard", SlaHours = 24,
                        RadiusKm = 5.0, CommissionRate = 0.12, PriceHint = "Standard rate",
                        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                    new DeliveryTierDto
                    {
                        Id = "9f1c0e6b-1b2a-5c3d-8e4f-0a1b2c3d4e5f", Name = "Express", SlaHours = 4,
                        RadiusKm = 8.0, CommissionRate = 0.15, PriceHint = "Faster dispatch",
                        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                };
                return Task.FromResult(Ok(JsonSerializer.Serialize(tiers, Json)));
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/api/v1/deliveries", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        """{"delivery_id":"seeded","status":"Ordered"}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(Ok("{}"));
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
