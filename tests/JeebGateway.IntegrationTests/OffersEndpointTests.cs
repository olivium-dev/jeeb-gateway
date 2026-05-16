using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BR-10 (T-backend-039): a Jeeber may have at most 2 active deliveries
/// (statuses accepted, picked_up, heading_off). Offer acceptance must
/// return 409 once the cap is hit, with a clear error message.
///
/// Tests share a single WebApplicationFactory and therefore a single
/// in-memory store across cases; each test scopes itself with unique
/// userIds and freshly-enqueued offers to avoid cross-test bleed.
/// </summary>
public class OffersEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OffersEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Accept_Returns_200_And_Transitions_Request_To_Accepted()
    {
        var jeeberId = $"jeeber-accept-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        var (jeeberClient, requestId, offerId) = await SeedOfferAsync(jeeberId, clientId);

        var resp = await jeeberClient.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        body!.Id.Should().Be(requestId);
        body.Status.Should().Be("accepted");
        body.JeeberId.Should().Be(jeeberId);
        body.AcceptedAt.Should().NotBeNull();

        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var stored = await offers.GetAsync(offerId, default);
        stored!.Status.Should().Be("accepted");
    }

    [Fact]
    public async Task Accept_When_Jeeber_At_Cap_Returns_409_With_BR10_Message()
    {
        var jeeberId = $"jeeber-cap-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        // Seed two already-accepted deliveries so the Jeeber is at the
        // BR-10 cap before the third accept attempt.
        await SeatActiveDeliveriesAsync(jeeberId, clientId, count: 2);

        var (jeeberClient, _, offerId) = await SeedOfferAsync(jeeberId, clientId);

        var resp = await jeeberClient.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be(
            "Maximum 2 active deliveries. Complete a delivery before accepting another.");
        problem.Status.Should().Be((int)HttpStatusCode.Conflict);
        problem.Type.Should().Be("https://jeeb.dev/errors/too-many-active-deliveries");
        problem.Detail.Should().Contain("limit 2");
    }

    [Fact]
    public async Task Accept_With_One_Active_Delivery_Still_Succeeds()
    {
        var jeeberId = $"jeeber-under-cap-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        await SeatActiveDeliveriesAsync(jeeberId, clientId, count: 1);

        var (jeeberClient, _, offerId) = await SeedOfferAsync(jeeberId, clientId);

        var resp = await jeeberClient.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Completed_Delivery_Frees_BR10_Slot()
    {
        var jeeberId = $"jeeber-completed-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        var seated = await SeatActiveDeliveriesAsync(jeeberId, clientId, count: 2);

        // Drop one of the two seeded deliveries to a terminal state so the
        // BR-10 cap is no longer hit.
        await MoveToStatus(seated[0], "delivered");

        var (jeeberClient, _, offerId) = await SeedOfferAsync(jeeberId, clientId);
        var resp = await jeeberClient.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accept_For_Other_Jeebers_Offer_Returns_403()
    {
        var ownerId = $"jeeber-owner-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        var (_, _, offerId) = await SeedOfferAsync(ownerId, clientId);

        // A different jeeber tries to claim the offer.
        var thief = JeeberClient($"jeeber-thief-{Guid.NewGuid()}");
        var resp = await thief.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accept_Unknown_Offer_Returns_404()
    {
        var client = JeeberClient($"jeeber-404-{Guid.NewGuid()}");
        var resp = await client.PostAsync($"/offers/{Guid.NewGuid()}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accept_When_Request_Already_Terminal_Returns_409()
    {
        var jeeberId = $"jeeber-race-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";

        var (jeeberClient, requestId, offerId) = await SeedOfferAsync(jeeberId, clientId);

        // Simulate the expiry sweeper or client cancellation moving the
        // request out of pre-acceptance before the accept lands.
        await MoveToStatus(requestId, "cancelled");

        var resp = await jeeberClient.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/request-not-acceptable");
    }

    [Fact]
    public async Task Accept_Without_Jeeber_Role_Returns_403()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var (_, _, offerId) = await SeedOfferAsync($"jeeber-role-{Guid.NewGuid()}", clientId);

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        var resp = await c.PostAsync($"/offers/{offerId}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private HttpClient JeeberClient(string jeeberId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private async Task<(HttpClient client, string requestId, string offerId)> SeedOfferAsync(
        string jeeberId, string clientId)
    {
        var requests = _factory.Services.GetRequiredService<IRequestsStore>();
        var created = await requests.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package"
        }, default);

        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var offer = offers.EnqueueForTest(jeeberId, created.Id);

        return (JeeberClient(jeeberId), created.Id, offer.Id);
    }

    private async Task<List<string>> SeatActiveDeliveriesAsync(string jeeberId, string clientId, int count)
    {
        var requests = _factory.Services.GetRequiredService<IRequestsStore>();
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var created = await requests.CreateAsync(new CreateRequestInput
            {
                ClientId = $"{clientId}-pre-{i}",
                Description = $"existing delivery {i}"
            }, default);

            var accepted = await requests.TryAcceptByJeeberAsync(
                created.Id,
                jeeberId,
                limit: int.MaxValue, // bypass the cap for setup
                at: DateTimeOffset.UtcNow,
                ct: default);
            accepted.Should().NotBeNull();
            ids.Add(created.Id);
        }
        return ids;
    }

    private async Task MoveToStatus(string requestId, string status)
    {
        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var ok = await store.SetStatusAsync(requestId, status, default);
        ok.Should().BeTrue();
    }

    private sealed record DeliveryRequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ScheduledAt,
        string? JeeberId,
        DateTimeOffset? AcceptedAt);
}
