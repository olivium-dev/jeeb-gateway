using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// GAP-2 (sprint-002, contract-freeze §3) — integration tests for the REQUEST-CENTRIC jeeber
/// discovery feed <c>GET /v1/jeebers/me/feed</c>. These pin the Gate B core check (an online
/// jeeber sees another client's pending request, <c>totalCount &gt;= 1</c>, <c>myOffer:null</c>),
/// the Gate B negative (offline jeeber → empty), the visibility predicate (a jeeber never sees
/// their own client requests), the <c>myOffer</c> annotation, the privacy stripping, and the
/// capability/identity authz.
///
/// Harness mirrors <see cref="AvailabilityEndpointTests"/>: real <see cref="Program"/> host;
/// header-based test identity (<c>X-User-Id</c> + <c>X-User-Roles: driver</c> → jeeber); the
/// delivery presence client swapped for the in-process <see cref="FakeDeliveryPresenceClient"/>
/// so the online-set is controllable without a live Go upstream.
/// </summary>
public class JeebFeedTests
{
    private const string FeedPath = "/v1/jeebers/me/feed";

    [Fact]
    public async Task Online_Jeeber_Sees_Other_Clients_Pending_Request_With_MyOffer_Null()
    {
        // Gate B core (contract-freeze §8 B): client created a pending request → an ONLINE jeeber's
        // feed returns it with myOffer:null (the fresh cross-able request). Goes online via the real
        // PUT /v1/jeebers/me/availability so this also exercises the GAP-1 fix + the online→feed join.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);

        var seeded = await SeedPendingRequestAsync(
            factory,
            clientId: "client-A",
            description: "Deliver documents to the bank",
            tierId: "express",
            pickupAddress: "Office, downtown", pickupLat: 33.51, pickupLng: 36.27,
            dropoffAddress: "Bank, Mazzeh", dropoffLat: 33.50, dropoffLng: 36.25);

        var goOnline = await jeeber.PutAsJsonAsync(
            "/v1/jeebers/me/availability",
            new { online = true, vehicleType = "car", zone = "downtown" });
        goOnline.StatusCode.Should().Be(HttpStatusCode.OK, "GAP-1 PUT v1 availability must resolve");

        var resp = await jeeber.GetAsync(FeedPath);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var feed = await resp.Content.ReadFromJsonAsync<FeedResponse>();
        feed!.TotalCount.Should().BeGreaterThanOrEqualTo(1);
        var item = feed.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.Status.Should().Be(RequestStatus.Pending);
        item.Description.Should().Be("Deliver documents to the bank");
        item.TierId.Should().Be("express");
        item.Pickup!.Address.Should().Be("Office, downtown");
        item.Pickup.Location!.Lat.Should().Be(33.51);
        item.MyOffer.Should().BeNull("a fresh cross-able request has no offer from this jeeber yet");
    }

    [Fact]
    public async Task Offline_Jeeber_Gets_Empty_Feed()
    {
        // Gate B negative (contract-freeze §8 C): an offline (never-online) jeeber → {items:[],total:0},
        // even though a cross-able pending request exists. 200, not an error.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out _);

        await SeedPendingRequestAsync(factory, clientId: "client-A", description: "Pending parcel");

        var resp = await jeeber.GetAsync(FeedPath);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var feed = await resp.Content.ReadFromJsonAsync<FeedResponse>();
        feed!.Items.Should().BeEmpty();
        feed.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Going_Offline_Empties_A_Previously_Populated_Feed()
    {
        // Gate B negative, transition form: online jeeber sees the request, then toggles OFF and the
        // feed empties — proves the online-gating join drives the feed both ways.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SeedPendingRequestAsync(factory, clientId: "client-A", description: "Pending parcel");

        await SetOnlineAsync(factory, jeeberId, online: true);
        var online = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();
        online!.TotalCount.Should().BeGreaterThanOrEqualTo(1, "an online jeeber sees the pending request");

        await SetOnlineAsync(factory, jeeberId, online: false);
        var offline = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();
        offline!.Items.Should().BeEmpty();
        offline.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Online_Jeeber_Does_Not_See_Their_Own_Client_Requests()
    {
        // Visibility predicate (contract-freeze §1.3 / §6.2): request.clientId != jeeberId.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);

        var foreign = await SeedPendingRequestAsync(factory, clientId: "client-A", description: "Other client parcel");
        var own = await SeedPendingRequestAsync(factory, clientId: jeeberId, description: "My own parcel as a client");

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();

        feed!.Items.Should().Contain(i => i.RequestId == foreign.Id);
        feed.Items.Should().NotContain(i => i.RequestId == own.Id, "a jeeber never sees their own client requests");
    }

    [Fact]
    public async Task Feed_Does_Not_Leak_Client_Pii()
    {
        // Privacy (contract-freeze §3.4): no clientId / recipientPhone / audioUrl / transcription / photos.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        await SeedPendingRequestAsync(
            factory, clientId: "client-secret-id", description: "Parcel",
            recipientPhone: "+9613000099");

        var raw = await (await jeeber.GetAsync(FeedPath)).Content.ReadAsStringAsync();

        raw.Should().NotContain("client-secret-id");
        raw.Should().NotContain("+9613000099");
        raw.Should().NotContain("recipientPhone");
        raw.Should().NotContain("clientId");
    }

    [Fact]
    public async Task Feed_Annotates_MyOffer_When_This_Jeeber_Already_Offered()
    {
        // Annotation path (contract-freeze §3.3 myOffer / §4): with the Offer upstream on and the
        // jeeber holding an offer on the request, the item carries myOffer (cents preserved).
        const string jeeberId = "jeeber-with-offer";
        var seededRequestId = Guid.NewGuid().ToString();
        var offers = new FakeOfferServiceClient(new List<JeeberFeedOffer>
        {
            new()
            {
                OfferId = "offer-1", RequestId = seededRequestId, Status = "submitted",
                FeeCents = 1250, EtaMinutes = 30, Note = "On my way",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        });

        using var factory = FactoryWithOffers(offers);
        var jeeber = JeeberClient(factory, jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        await SeedPendingRequestAsync(factory, clientId: "client-A", description: "Parcel", id: seededRequestId);

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();

        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seededRequestId).Subject;
        item.MyOffer.Should().NotBeNull();
        item.MyOffer!.OfferId.Should().Be("offer-1");
        item.MyOffer.Status.Should().Be("submitted");
        item.MyOffer.FeeCents.Should().Be(1250);
        item.MyOffer.EtaMinutes.Should().Be(30);
    }

    // ── G1 (owner-approved 2026-07-21): additive display-safe sender identity ─────────

    [Fact]
    public async Task Feed_Includes_Display_Safe_Sender_Identity_When_Profile_Resolves()
    {
        // G1: the item carries a SHORT name (given + last initial) and the absolute-https avatar
        // resolved from the request's client profile via the gateway's existing user-profile lookup.
        const string clientId = "client-A";
        var users = new FakeUsersStore(new Dictionary<string, UserProfile>
        {
            [clientId] = Profile(clientId, "Nour Khaled", "https://cdn.jeeb.app/a/nour.png"),
        });

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedPendingRequestAsync(factory, clientId: clientId, description: "Parcel");

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();

        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.SenderName.Should().Be("Nour K.");
        item.SenderAvatarUrl.Should().Be("https://cdn.jeeb.app/a/nour.png");
    }

    [Fact]
    public async Task Feed_Sender_Short_Name_Is_Given_Name_Only_For_A_Single_Token()
    {
        // A single-token name is already just a given name — returned as-is, no trailing initial.
        const string clientId = "client-solo";
        var users = new FakeUsersStore(new Dictionary<string, UserProfile>
        {
            [clientId] = Profile(clientId, "Nour"),
        });

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedPendingRequestAsync(factory, clientId: clientId, description: "Parcel");

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();
        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.SenderName.Should().Be("Nour");
        item.SenderAvatarUrl.Should().BeNull("this profile has no avatar");
    }

    [Fact]
    public async Task Feed_Sender_Avatar_Is_Dropped_When_Not_Absolute_Https()
    {
        // A non-https (or relative) avatar is not display-safe → dropped; the short name still resolves.
        const string clientId = "client-http";
        var users = new FakeUsersStore(new Dictionary<string, UserProfile>
        {
            [clientId] = Profile(clientId, "Sara Mansour", "http://cdn.jeeb.app/a/sara.png"),
        });

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedPendingRequestAsync(factory, clientId: clientId, description: "Parcel");

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();
        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.SenderName.Should().Be("Sara M.");
        item.SenderAvatarUrl.Should().BeNull("a non-https avatar is not projected");
    }

    [Fact]
    public async Task Feed_Sender_Identity_Degrades_To_Null_When_Lookup_Throws()
    {
        // Degrade-don't-fail: a profile-lookup fault for the request's client must NOT fail the feed.
        const string clientId = "client-boom";
        var users = new FakeUsersStore(throwFor: new[] { clientId });

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedPendingRequestAsync(factory, clientId: clientId, description: "Parcel");

        var resp = await jeeber.GetAsync(FeedPath);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a sender-lookup blip degrades, never 5xx");

        var feed = await resp.Content.ReadFromJsonAsync<FeedResponse>();
        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.SenderName.Should().BeNull();
        item.SenderAvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Feed_Sender_Identity_Is_Null_When_Profile_Not_Found()
    {
        // Unknown client (no profile row) → null sender fields, the item is still served.
        var users = new FakeUsersStore();

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        var seeded = await SeedPendingRequestAsync(factory, clientId: "client-unknown", description: "Parcel");

        var feed = await (await jeeber.GetAsync(FeedPath)).Content.ReadFromJsonAsync<FeedResponse>();
        var item = feed!.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.SenderName.Should().BeNull();
        item.SenderAvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Feed_Never_Serializes_Raw_ClientId_Even_When_Sender_Identity_Resolves()
    {
        // Strongest privacy pin: WITH a resolving profile the display name is present in the JSON,
        // but the raw clientId (and the "clientId" key) never are.
        const string clientId = "client-secret-id-xyz";
        var users = new FakeUsersStore(new Dictionary<string, UserProfile>
        {
            [clientId] = Profile(clientId, "Nour Khaled", "https://cdn.jeeb.app/a/nour.png"),
        });

        using var factory = FactoryWithUsers(users);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetOnlineAsync(factory, jeeberId, online: true);
        await SeedPendingRequestAsync(factory, clientId: clientId, description: "Parcel");

        var raw = await (await jeeber.GetAsync(FeedPath)).Content.ReadAsStringAsync();

        raw.Should().Contain("Nour K.", "the display-safe sender name is projected");
        raw.Should().Contain("senderName");
        raw.Should().NotContain(clientId, "the raw clientId must never appear in the feed payload");
        raw.Should().NotContain("clientId");
    }

    [Fact]
    public async Task Client_Without_Jeeber_Capability_Gets_403()
    {
        // Authz negative (contract-freeze §3.5): jeeber.feed.read is jeeber-only; a client → 403.
        using var factory = Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"client-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync(FeedPath);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Missing_Identity_Returns_401()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync(FeedPath);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(UseFakeDeliveryPresence));

    private static WebApplicationFactory<Program> FactoryWithOffers(FakeOfferServiceClient offers) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Offer upstream ON so the feed performs the myOffer annotation call.
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["FeatureFlags:UseUpstream:Offer"] = "true" }));
            builder.ConfigureServices(services =>
            {
                UseFakeDeliveryPresence(services);
                services.RemoveAll<IOfferServiceClient>();
                services.AddSingleton<IOfferServiceClient>(offers);
            });
        });

    private static WebApplicationFactory<Program> FactoryWithUsers(IUsersStore users) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                UseFakeDeliveryPresence(services);
                // Swap the user-profile store so the G1 sender-identity lookup is controllable
                // (mirrors the delivery-presence / offer swaps above).
                services.RemoveAll<IUsersStore>();
                services.AddSingleton<IUsersStore>(users);
            }));

    private static UserProfile Profile(string id, string name, string? avatarUrl = null) =>
        new() { Id = id, Phone = string.Empty, Name = name, AvatarUrl = avatarUrl };

    private static void UseFakeDeliveryPresence(IServiceCollection services)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, out string jeeberId)
    {
        jeeberId = $"jeeber-{Guid.NewGuid()}";
        return JeeberClient(factory, jeeberId);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // opaque driver → canonical jeeber
        return client;
    }

    private static async Task SetOnlineAsync(WebApplicationFactory<Program> factory, string jeeberId, bool online)
    {
        // Drive the canonical presence store directly (the same store the toggle writes), so the
        // feed's online gate (delivery GetAvailabilityAsync) flips without going through the toggle.
        var delivery = (FakeDeliveryPresenceClient)factory.Services.GetRequiredService<IDeliveryServiceClient>();
        await delivery.SetAvailabilityAsync(
            new JeeberAvailabilityUpstreamRequest { Online = online, VehicleType = "car", Zone = "downtown" },
            jeeberId,
            default);
    }

    private static async Task<DeliveryRequest> SeedPendingRequestAsync(
        WebApplicationFactory<Program> factory,
        string clientId,
        string description,
        string? id = null,
        string? tierId = null,
        string? pickupAddress = null, double? pickupLat = null, double? pickupLng = null,
        string? dropoffAddress = null, double? dropoffLat = null, double? dropoffLng = null,
        string? recipientPhone = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        return await store.CreateAsync(
            new CreateRequestInput
            {
                Id = id,
                ClientId = clientId,
                Description = description,
                TierId = tierId,
                PickupAddress = pickupAddress,
                PickupLocation = pickupLat is { } plat && pickupLng is { } plng ? new GeoPoint { Lat = plat, Lng = plng } : null,
                DropoffAddress = dropoffAddress,
                DropoffLocation = dropoffLat is { } dlat && dropoffLng is { } dlng ? new GeoPoint { Lat = dlat, Lng = dlng } : null,
                RecipientPhone = recipientPhone,
            },
            default);
    }

    // ── wire DTOs for assertions (camelCase via Web defaults) ─────────────────────────

    private sealed record FeedResponse(List<FeedItem> Items, int TotalCount);

    private sealed record FeedItem(
        string RequestId,
        string Status,
        string Description,
        FeedLoc? Pickup,
        FeedLoc? Dropoff,
        string? TierId,
        double? DistanceMeters,
        DateTimeOffset CreatedAt,
        FeedOffer? MyOffer,
        string? SenderName,
        string? SenderAvatarUrl);

    private sealed record FeedLoc(string? Address, FeedPoint? Location);

    private sealed record FeedPoint(double Lat, double Lng);

    private sealed record FeedOffer(string OfferId, string Status, long FeeCents, int EtaMinutes, string? Note, DateTimeOffset? CreatedAt);

    /// <summary>
    /// Minimal in-process <see cref="IOfferServiceClient"/> fake: only
    /// <see cref="ListOffersForJeeberAsync"/> carries behaviour (returns the configured offers);
    /// every auction-mutation method is unsupported so an accidental call is loud.
    /// </summary>
    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        private readonly IReadOnlyList<JeeberFeedOffer> _offers;

        public FakeOfferServiceClient(IReadOnlyList<JeeberFeedOffer> offers) => _offers = offers;

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult(_offers);

        public Task<RequestMirrorResult> MirrorRequestAsync(string actingUserId, string requestId, string clientId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(string actingUserId, string requestId, string offerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferAcceptWire> AcceptAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferAcceptResult> AcceptWithStatusAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(string actingUserId, string offerId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// Controllable <see cref="IUsersStore"/> for the G1 sender-identity path: <see cref="GetByIdAsync"/>
    /// returns a configured profile, is absent (null) for unknown ids, or throws for ids in
    /// <c>throwFor</c> (the degrade-don't-fail case). Every other member — none of which the feed
    /// request touches — delegates to a throwaway in-memory store so an incidental pipeline call
    /// still behaves, keeping <see cref="GetByIdAsync"/> the single controllable seam.
    /// </summary>
    private sealed class FakeUsersStore : IUsersStore
    {
        private readonly InMemoryUsersStore _inner = new();
        private readonly Dictionary<string, UserProfile> _profiles;
        private readonly HashSet<string> _throwFor;

        public FakeUsersStore(
            IReadOnlyDictionary<string, UserProfile>? profiles = null,
            IEnumerable<string>? throwFor = null)
        {
            _profiles = profiles is null
                ? new Dictionary<string, UserProfile>(StringComparer.Ordinal)
                : new Dictionary<string, UserProfile>(profiles, StringComparer.Ordinal);
            _throwFor = new HashSet<string>(throwFor ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
        {
            if (_throwFor.Contains(userId))
            {
                throw new InvalidOperationException($"forced user-lookup failure for {userId}");
            }

            _profiles.TryGetValue(userId, out var profile);
            return Task.FromResult<UserProfile?>(profile);
        }

        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct) => _inner.GetOrCreateAsync(userId, ct);
        public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct) => _inner.UpsertProjectionAsync(profile, ct);
        public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct) => _inner.UpdateProfileAsync(userId, patch, ct);
        public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct) => _inner.ListAddressesAsync(userId, ct);
        public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct) => _inner.GetAddressAsync(userId, addressId, ct);
        public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct) => _inner.CreateAddressAsync(userId, input, ct);
        public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct) => _inner.UpdateAddressAsync(userId, addressId, patch, ct);
        public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct) => _inner.DeleteAddressAsync(userId, addressId, ct);
        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct) => _inner.SearchAsync(query, ct);
        public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct) => _inner.SuspendAsync(userId, reason, adminId, ct);
        public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct) => _inner.UnsuspendAsync(userId, adminId, ct);
        public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct) => _inner.SwitchRoleAsync(userId, newRole, ct);
        public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct) => _inner.GrantRoleAsync(userId, role, ct);
        public Task<bool> PurgePiiAsync(string userId, CancellationToken ct) => _inner.PurgePiiAsync(userId, ct);
    }
}
