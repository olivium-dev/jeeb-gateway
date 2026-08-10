using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P7 — T2: the OFFER-WAIT ENVELOPE SHAPE, pinned as golden JSON.
///
/// <para>These assertions pin the NEW contract on its own terms — they are deliberately
/// NOT written as a diff against the old shape. The wire change is ADDITIVE:
/// <c>broadcastExpiresAt</c> was never emitted by this gateway, so nothing an old
/// consumer reads disappears; what is new is that every listed surface now carries a
/// <c>serverNow</c> and (where a countdown applies) an
/// <c>offerDeadlineAt</c>/<c>offerDeadlineInSeconds</c> pair.</para>
///
/// <para>The client contract is asymmetric by design: a MISSING
/// <c>offerDeadlineInSeconds</c> on a live pre-acceptance row is a client-side
/// contract violation. That is why these tests assert PRESENCE, not merely
/// "parses without error".</para>
/// </summary>
public class OfferDeadlineContractTests
{
    private const int UrgentTtlSeconds = 30 * 60;

    // ── T2.1 — pending single-read ───────────────────────────────────────────

    [Fact]
    public async Task T2_1_Pending_Request_Carries_ServerNow_And_A_Full_Urgent_Countdown()
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var json = await GetJsonAsync(factory, clientId, $"/v1/requests/{seeded.Id}");

        // serverNow: present, non-null, a real instant.
        var serverNow = json.GetProperty("serverNow").GetDateTimeOffset();
        serverNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        // offerDeadlineAt == createdAt + the urgent tier TTL.
        var createdAt = json.GetProperty("createdAt").GetDateTimeOffset();
        json.GetProperty("offerDeadlineAt").GetDateTimeOffset()
            .Should().Be(createdAt + TimeSpan.FromSeconds(UrgentTtlSeconds));

        // offerDeadlineInSeconds ∈ (0, 1800]  — THE value the client anchors on.
        var remaining = json.GetProperty("offerDeadlineInSeconds").GetInt32();
        remaining.Should().BePositive();
        remaining.Should().BeLessThanOrEqualTo(UrgentTtlSeconds);

        json.GetProperty("expiredAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── T2.2 — accepted single-read: no countdown applies ────────────────────

    [Fact]
    public async Task T2_2_Accepted_Request_Has_ServerNow_But_Both_Deadline_Fields_Null()
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.SetStatusAsync(seeded.Id, RequestStatus.Accepted, default)).Should().BeTrue();

        var json = await GetJsonAsync(factory, clientId, $"/v1/requests/{seeded.Id}");

        json.GetProperty("serverNow").ValueKind.Should().NotBe(JsonValueKind.Null);
        json.GetProperty("offerDeadlineAt").ValueKind.Should().Be(JsonValueKind.Null,
            "no countdown applies once an offer has been accepted");
        json.GetProperty("offerDeadlineInSeconds").ValueKind.Should().Be(JsonValueKind.Null,
            "offerDeadlineInSeconds is null EXACTLY when offerDeadlineAt is");
    }

    // ── T2.3 — expired single-read ───────────────────────────────────────────

    [Fact]
    public async Task T2_3_Expired_Request_Has_Null_Deadlines_And_A_NonNull_ExpiredAt()
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var expiredAt = DateTimeOffset.UtcNow;
        (await store.TryExpireAsync(seeded.Id, expiredAt, default)).Should().BeTrue();

        var json = await GetJsonAsync(factory, clientId, $"/v1/requests/{seeded.Id}");

        json.GetProperty("offerDeadlineAt").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("offerDeadlineInSeconds").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("expiredAt").ValueKind.Should().NotBe(JsonValueKind.Null,
            "the sweeper's terminal stamp is what the client reads to explain the 0:00");
    }

    // ── T2.4 — the paged list envelope ───────────────────────────────────────

    [Fact]
    public async Task T2_4_List_Envelope_Carries_ServerNow_Once_And_Deadlines_Per_Live_Item()
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var pending = await SeedAsync(factory, clientId, tierId: "urgent");
        var terminal = await SeedAsync(factory, clientId, tierId: "urgent");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.TryExpireAsync(terminal.Id, DateTimeOffset.UtcNow, default)).Should().BeTrue();

        var json = await GetJsonAsync(factory, clientId, "/v1/requests?role=client");

        // serverNow lives ONCE, at the top level of the envelope.
        json.GetProperty("serverNow").ValueKind.Should().NotBe(JsonValueKind.Null);

        var items = json.GetProperty("items").EnumerateArray().ToList();
        foreach (var item in items)
        {
            item.TryGetProperty("serverNow", out _).Should().BeFalse(
                "serverNow is an ENVELOPE member — repeating it per item would invite "
                + "per-row clock reads and re-open the drift this contract closes");
        }

        var pendingItem = items.Single(i => i.GetProperty("id").GetString() == pending.Id);
        pendingItem.GetProperty("offerDeadlineInSeconds").GetInt32()
            .Should().BePositive().And.BeLessThanOrEqualTo(UrgentTtlSeconds);
        pendingItem.TryGetProperty("offerDeadlineAt", out _).Should().BeTrue();

        var terminalItem = items.Single(i => i.GetProperty("id").GetString() == terminal.Id);
        terminalItem.TryGetProperty("offerDeadlineAt", out _).Should().BeFalse(
            "terminal rows omit the deadline pair entirely (WhenWritingNull)");
        terminalItem.TryGetProperty("offerDeadlineInSeconds", out _).Should().BeFalse();
    }

    // ── T2.5 / T2.6 — the jeeber feed envelope ───────────────────────────────

    [Fact]
    public async Task T2_5_Online_Jeeber_Feed_Item_Carries_Both_Deadline_Fields()
    {
        using var factory = Factory();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedAsync(factory, $"client-{Guid.NewGuid()}", tierId: "urgent");

        var json = await GetJsonAsync(factory, jeeberId, "/v1/jeebers/me/feed", role: "driver");

        json.GetProperty("serverNow").ValueKind.Should().NotBe(JsonValueKind.Null);
        json.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var item = json.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("requestId").GetString() == seeded.Id);

        item.GetProperty("offerDeadlineInSeconds").GetInt32()
            .Should().BePositive().And.BeLessThanOrEqualTo(UrgentTtlSeconds);
        var createdAt = item.GetProperty("createdAt").GetDateTimeOffset();
        item.GetProperty("offerDeadlineAt").GetDateTimeOffset()
            .Should().Be(createdAt + TimeSpan.FromSeconds(UrgentTtlSeconds));
    }

    [Fact]
    public async Task T2_6_Offline_Jeeber_Empty_Feed_Still_Carries_A_NonNull_ServerNow()
    {
        // Proves JeeberFeedResponse.EmptyAt(now) replaced the old static Empty singleton —
        // an unstamped envelope is no longer constructible.
        using var factory = Factory();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        await SeedAsync(factory, $"client-{Guid.NewGuid()}", tierId: "urgent");

        var json = await GetJsonAsync(factory, jeeberId, "/v1/jeebers/me/feed", role: "driver");

        json.GetProperty("serverNow").ValueKind.Should().NotBe(JsonValueKind.Null);
        json.GetProperty("items").GetArrayLength().Should().Be(0);
        json.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    // ── T2.7 — no legacy alias was reintroduced ──────────────────────────────

    [Theory]
    [InlineData("broadcastExpiresAt")]
    [InlineData("windowExpiresAt")]
    [InlineData("offerWindowExpiresAt")]
    public async Task T2_7_Pending_Dto_Contains_No_Legacy_Deadline_Alias(string legacyKey)
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var raw = await GetRawAsync(factory, clientId, $"/v1/requests/{seeded.Id}");

        raw.Should().NotContain(legacyKey,
            "the clean break has exactly ONE countdown key on the wire: offerDeadlineInSeconds");
    }

    [Fact]
    public async Task T2_7b_Pending_Dto_Has_No_Bare_ExpiresAt_Key()
    {
        using var factory = Factory();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var json = await GetJsonAsync(factory, clientId, $"/v1/requests/{seeded.Id}");

        json.TryGetProperty("expiresAt", out _).Should().BeFalse(
            "`expiresAt` was one of the ambiguous aliases the mobile dual-read chained "
            + "through; the contract names the field offerDeadlineAt");
    }

    // ── T2.8 — an upstream tier-catalog blip must not 5xx a read ─────────────

    [Fact]
    public async Task T2_8_Faulted_Tier_Upstream_Still_Returns_200_With_A_Local_Catalog_Deadline()
    {
        // FakeDeliveryPresenceClient.ListTiersAsync throws — with the Delivery upstream ON
        // that is exactly the "delivery-service blip" case. The projector must degrade to
        // the LOCAL catalog (urgent = 1800s), never fail the read.
        using var factory = FactoryWithDeliveryUpstreamOn();
        var clientId = $"client-{Guid.NewGuid()}";
        var seeded = await SeedAsync(factory, clientId, tierId: "urgent");

        var resp = await ClientFor(factory, clientId).GetAsync($"/v1/requests/{seeded.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a read never 5xxes on an upstream blip");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("offerDeadlineInSeconds").GetInt32()
            .Should().BePositive().And.BeLessThanOrEqualTo(UrgentTtlSeconds,
                "the local catalog is the degrade target, so the urgent TTL still resolves");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(UseFakeDeliveryPresence));

    private static WebApplicationFactory<Program> FactoryWithDeliveryUpstreamOn() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["FeatureFlags:UseUpstream:Delivery"] = "true" }));
            builder.ConfigureServices(UseFakeDeliveryPresence);
        });

    private static void UseFakeDeliveryPresence(IServiceCollection services)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
    }

    private static HttpClient ClientFor(
        WebApplicationFactory<Program> factory, string userId, string role = "customer")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private static async Task<string> GetRawAsync(
        WebApplicationFactory<Program> factory, string userId, string path, string role = "customer")
    {
        var resp = await ClientFor(factory, userId, role).GetAsync(path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> GetJsonAsync(
        WebApplicationFactory<Program> factory, string userId, string path, string role = "customer")
        => JsonDocument.Parse(await GetRawAsync(factory, userId, path, role)).RootElement;

    private static Task<DeliveryRequest> SeedAsync(
        WebApplicationFactory<Program> factory, string clientId, string tierId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        return store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "P7 contract row",
            TierId = tierId,
            PickupAddress = "Office, downtown",
            PickupLocation = new GeoPoint { Lat = 33.51, Lng = 36.27 },
            DropoffAddress = "Bank, Mazzeh",
            DropoffLocation = new GeoPoint { Lat = 33.50, Lng = 36.25 },
        }, default);
    }

    private static async Task SetOnlineAsync(
        WebApplicationFactory<Program> factory, string jeeberId, bool online)
    {
        var delivery = (FakeDeliveryPresenceClient)
            factory.Services.GetRequiredService<IDeliveryServiceClient>();
        await delivery.SetAvailabilityAsync(
            new JeeberAvailabilityUpstreamRequest
            {
                Online = online, VehicleType = "car", Zone = "downtown",
                Lat = 33.51, Lng = 36.27,
            },
            jeeberId,
            default);
    }
}
