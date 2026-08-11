using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Counterparty display identity on GET /v1/deliveries/{id} — the ONE participant-scoped
/// read both chat legs and every mutual-rating entry point can reach. Before this, the DTO
/// carried <c>jeeberName</c> only: no jeeber avatar and no client identity at all, so the
/// chat header and both rating screens fell back to a letter placeholder while the offer
/// card (which HAS the enrichment) showed the real photo.
///
/// The contract is the same additive/ignore-when-null one <c>amount</c>/<c>jeeberName</c>
/// already follow: resolved ⇒ present, unresolved ⇒ key ABSENT, store fault ⇒ still 200.
/// Avatars are projected through <see cref="AvatarUrlResolver"/>, never emitted as the bare
/// stored object ref.
/// </summary>
public class DeliveryCounterpartyIdentityTests
{
    private const string PublicBaseUrl = "http://192.168.2.39:10090";
    private const string JeeberRef = "profile_avatar/7dcb45dffd1e4acc9cc23996198f7f99.jpg";
    private const string ClientRef = "profile_avatar/aa11bb22cc33dd44ee55ff6677889900.jpg";

    [Fact]
    public async Task ClientRead_CarriesTheJeeberNameAndAbsolutizedJeeberAvatar()
    {
        using var factory = Factory();
        var (clientId, jeeberId, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: JeeberRef, clientAvatar: ClientRef);

        var resp = await HeaderClient(factory, clientId, "customer").GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CounterpartyDto>();
        dto!.JeeberName.Should().Be("Karim Jeeber");
        dto.JeeberAvatarUrl.Should().Be($"{PublicBaseUrl}/api/users/{jeeberId}/avatar?v=7dcb45dffd1e",
            "the client's chat header and rating screen need a LOADABLE url, not the stored object ref");
    }

    [Fact]
    public async Task JeeberRead_CarriesTheClientNameAndAbsolutizedClientAvatar()
    {
        using var factory = Factory();
        var (clientId, jeeberId, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: JeeberRef, clientAvatar: ClientRef);

        var resp = await HeaderClient(factory, jeeberId, "driver")
            .GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CounterpartyDto>();
        dto!.ClientName.Should().Be("Nour Client");
        dto.ClientAvatarUrl.Should().Be($"{PublicBaseUrl}/api/users/{clientId}/avatar?v=aa11bb22cc33",
            "the jeeber leg had NO counterparty surface at all before this");
    }

    [Fact]
    public async Task NoAvatarAndNoName_OmitsTheKeysEntirely()
    {
        using var factory = Factory();
        var (clientId, _, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: null, clientAvatar: null, withNames: false);

        var resp = await HeaderClient(factory, clientId, "customer").GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("\"jeeberAvatarUrl\"");
        body.Should().NotContain("\"clientAvatarUrl\"");
        body.Should().NotContain("\"clientName\"");
        body.Should().NotContain("\"jeeberName\"");
    }

    [Fact]
    public async Task StoredValueThatIsNotAProjectableRef_IsNeverEchoed()
    {
        using var factory = Factory();
        var (clientId, _, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: "old-avatar.png", clientAvatar: "old-avatar.png");

        var resp = await HeaderClient(factory, clientId, "customer").GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("old-avatar.png");
    }

    [Fact]
    public async Task UsersStoreFault_StillServes200_WithoutTheIdentityFields()
    {
        // Degrade-don't-fail: this read backs the chat summary, delivery detail and
        // live tracking — a users-store blip must never turn it into a 5xx.
        using var factory = Factory(faultUsers: true);
        var (clientId, _, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: JeeberRef, clientAvatar: ClientRef, faultAfterSeed: true);

        var resp = await HeaderClient(factory, clientId, "customer").GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("\"jeeberAvatarUrl\"");
        body.Should().NotContain("\"clientAvatarUrl\"");
    }

    [Fact]
    public async Task NonParticipant_Gets403_AndLeaksNoIdentity()
    {
        using var factory = Factory();
        var (_, _, deliveryId) = await SeedAssignedDelivery(
            factory, jeeberAvatar: JeeberRef, clientAvatar: ClientRef);

        var resp = await HeaderClient(factory, $"stranger-{Guid.NewGuid():N}", "customer")
            .GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Nour Client");
        body.Should().NotContain("Karim Jeeber");
        body.Should().NotContain("/avatar?v=");
    }

    // =========================================================================
    // helpers
    // =========================================================================

    private static async Task<(string ClientId, string JeeberId, string DeliveryId)> SeedAssignedDelivery(
        WebApplicationFactory<Program> factory,
        string? jeeberAvatar,
        string? clientAvatar,
        bool withNames = true,
        bool faultAfterSeed = false)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        var jeeberId = $"jeeber-{Guid.NewGuid():N}";

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var users = factory.Services.GetRequiredService<IUsersStore>();

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "counterparty parcel",
        }, CancellationToken.None);

        (await store.SetJeeberIdAsync(created.Id, jeeberId, CancellationToken.None)).Should().BeTrue();

        await users.UpsertProjectionAsync(new UserProfile
        {
            Id = jeeberId,
            Phone = "+9613100077",
            Name = withNames ? "Karim Jeeber" : string.Empty,
            AvatarUrl = jeeberAvatar,
        }, CancellationToken.None);
        await users.UpsertProjectionAsync(new UserProfile
        {
            Id = clientId,
            Phone = "+96171880110",
            Name = withNames ? "Nour Client" : string.Empty,
            AvatarUrl = clientAvatar,
        }, CancellationToken.None);

        if (faultAfterSeed && users is FaultingUsersStore faulting)
        {
            faulting.Fault = true;
        }

        return (clientId, jeeberId, created.Id);
    }

    private static WebApplicationFactory<Program> Factory(bool faultUsers = false)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:PublicBaseUrl", PublicBaseUrl);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "FeatureFlags:UseUpstream:Offer", "false" },
                    { "FeatureFlags:UseUpstream:Delivery", "false" },
                }));
            builder.ConfigureTestServices(services =>
            {
                Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
                services.Configure<UpstreamFeatureFlags>(f => f.Delivery = false);

                if (faultUsers)
                {
                    services.RemoveAll<IUsersStore>();
                    services.AddSingleton<IUsersStore>(new FaultingUsersStore());
                }
            });
        });

    private static HttpClient HeaderClient(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private sealed class CounterpartyDto
    {
        [JsonPropertyName("jeeberName")] public string? JeeberName { get; set; }
        [JsonPropertyName("jeeberAvatarUrl")] public string? JeeberAvatarUrl { get; set; }
        [JsonPropertyName("clientName")] public string? ClientName { get; set; }
        [JsonPropertyName("clientAvatarUrl")] public string? ClientAvatarUrl { get; set; }
    }

    /// <summary>Users store that starts healthy (so the seed lands) and then throws on every read.</summary>
    private sealed class FaultingUsersStore : InMemoryUsersStore, IUsersStore
    {
        public bool Fault { get; set; }

        Task<UserProfile?> IUsersStore.GetByIdAsync(string userId, CancellationToken ct)
            => Fault ? throw new InvalidOperationException("users store down") : base.GetByIdAsync(userId, ct);
    }
}
