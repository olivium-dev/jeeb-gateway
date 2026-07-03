using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Push;
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
///     tierId against the unified catalog: unknown → 400 with the machine-readable
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
    public async Task V1Create_UnknownTierId_Returns400TierNotFound_AndPublishesNothing()
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync(
            "/v1/requests", ValidPayload("Pick up keys", "platinum_super_fast"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
}
