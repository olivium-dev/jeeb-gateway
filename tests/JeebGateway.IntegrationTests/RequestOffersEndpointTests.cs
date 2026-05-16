using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Offer submission and management endpoints (T-backend-010 / JEEB-28).
/// Covers the full set of acceptance criteria:
/// <list type="bullet">
///   <item>POST /requests/{id}/offers — happy path, fee >= $1, valid eta.</item>
///   <item>409 once a request hits 20 live offers.</item>
///   <item>409 when the same Jeeber tries to submit a second live offer.</item>
///   <item>DELETE /requests/{id}/offers/{offerId} — withdraw, allows re-offer.</item>
///   <item>Realtime "new offer" event dispatched on every accepted submission.</item>
/// </list>
///
/// Tests share a single WebApplicationFactory and therefore the same
/// in-memory stores; each test scopes itself with unique requestIds /
/// userIds to keep state isolated.
/// </summary>
public class RequestOffersEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestOffersEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Submit_Returns_201_With_Pending_Offer_And_Fires_WS_Event()
    {
        var (clientId, requestId) = await SeedRequestAsync();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var jeeberClient = JeeberClient(jeeberId);

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 12.5m, etaMinutes = 30, note = "Heading that way" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<OfferDto>();
        dto!.RequestId.Should().Be(requestId);
        dto.JeeberId.Should().Be(jeeberId);
        dto.Status.Should().Be("pending");
        dto.Fee.Should().Be(12.5m);
        dto.EtaMinutes.Should().Be(30);
        dto.Note.Should().Be("Heading that way");

        var realtime = _factory.Services.GetRequiredService<InMemoryOfferRealtimeNotifier>();
        realtime.Events.Should().Contain(e =>
            e.OfferId == dto.Id
            && e.RequestId == requestId
            && e.ClientId == clientId
            && e.JeeberId == jeeberId);
    }

    [Fact]
    public async Task Submit_With_Fee_Below_Floor_Returns_400()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 0.50m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-fee-too-low");
    }

    [Fact]
    public async Task Submit_At_Exactly_One_Dollar_Succeeds()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 1m, etaMinutes = 15 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_Without_Eta_Returns_400()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-eta-invalid");
    }

    [Fact]
    public async Task Submit_For_Unknown_Request_Returns_404()
    {
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");
        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{Guid.NewGuid()}/offers",
            new { fee = 5m, etaMinutes = 20 });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submit_By_Client_Role_Returns_403()
    {
        var (clientId, requestId) = await SeedRequestAsync();
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await c.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Submit_On_Own_Request_Returns_409_BR1()
    {
        // The seeded clientId is the request owner — using it as the Jeeber
        // must trip the BR-1 same-delivery rule even though the user has the
        // driver role here for the request.
        var (clientId, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient(clientId);

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/same-delivery-role-violation");
    }

    [Fact]
    public async Task Submit_Twice_From_Same_Jeeber_Without_Withdraw_Returns_409()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var jeeberClient = JeeberClient(jeeberId);

        var first = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 7m, etaMinutes = 25 });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-already-exists");
    }

    [Fact]
    public async Task Twenty_First_Offer_From_Distinct_Jeebers_Returns_409()
    {
        var (_, requestId) = await SeedRequestAsync();

        for (var i = 0; i < 20; i++)
        {
            var jeeber = JeeberClient($"jeeber-{i}-{Guid.NewGuid()}");
            var ok = await jeeber.PostAsJsonAsync(
                $"/requests/{requestId}/offers",
                new { fee = 5m, etaMinutes = 20 });
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"bid {i} should land");
        }

        var twentyFirst = JeeberClient($"jeeber-21-{Guid.NewGuid()}");
        var resp = await twentyFirst.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offers-per-request-exceeded");
        problem.Detail.Should().Contain("20");
    }

    [Fact]
    public async Task Withdraw_Returns_204_And_Allows_Re_Offer()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var jeeberClient = JeeberClient(jeeberId);

        var firstSubmit = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });
        var firstOffer = (await firstSubmit.Content.ReadFromJsonAsync<OfferDto>())!;

        var withdraw = await jeeberClient.DeleteAsync(
            $"/requests/{requestId}/offers/{firstOffer.Id}");
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-offer must succeed because the previous offer is now Withdrawn.
        var secondSubmit = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 7m, etaMinutes = 25 });
        secondSubmit.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondOffer = (await secondSubmit.Content.ReadFromJsonAsync<OfferDto>())!;
        secondOffer.Id.Should().NotBe(firstOffer.Id);
        secondOffer.Fee.Should().Be(7m);

        // Two WS events expected (one per accepted submission).
        var realtime = _factory.Services.GetRequiredService<InMemoryOfferRealtimeNotifier>();
        realtime.Events.Should().Contain(e => e.OfferId == firstOffer.Id);
        realtime.Events.Should().Contain(e => e.OfferId == secondOffer.Id);
    }

    [Fact]
    public async Task Withdraw_Twice_Returns_409()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");
        var submit = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });
        var offer = (await submit.Content.ReadFromJsonAsync<OfferDto>())!;

        (await jeeberClient.DeleteAsync($"/requests/{requestId}/offers/{offer.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await jeeberClient.DeleteAsync(
            $"/requests/{requestId}/offers/{offer.Id}");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-pending");
    }

    [Fact]
    public async Task Withdraw_By_Different_Jeeber_Returns_403()
    {
        var (_, requestId) = await SeedRequestAsync();
        var owner = JeeberClient($"jeeber-owner-{Guid.NewGuid()}");
        var submit = await owner.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });
        var offer = (await submit.Content.ReadFromJsonAsync<OfferDto>())!;

        var thief = JeeberClient($"jeeber-thief-{Guid.NewGuid()}");
        var resp = await thief.DeleteAsync(
            $"/requests/{requestId}/offers/{offer.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-owned");
    }

    [Fact]
    public async Task Withdraw_Unknown_Offer_Returns_404()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.DeleteAsync(
            $"/requests/{requestId}/offers/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submit_When_Request_Already_Accepted_Returns_409()
    {
        var (_, requestId) = await SeedRequestAsync();
        var existingJeeber = $"jeeber-existing-{Guid.NewGuid()}";

        // Move the request out of the pre-acceptance set by binding it to
        // another Jeeber. The submit endpoint must refuse new bids.
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
            var accepted = await store.TryAcceptByJeeberAsync(
                requestId, existingJeeber, limit: int.MaxValue,
                at: DateTimeOffset.UtcNow, ct: default);
            accepted.Should().NotBeNull();
        }

        var lateJeeber = JeeberClient($"jeeber-late-{Guid.NewGuid()}");
        var resp = await lateJeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/request-not-open-for-offers");
    }

    [Fact]
    public async Task Submit_Without_Identity_Returns_401()
    {
        var (_, requestId) = await SeedRequestAsync();
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submit_With_Oversize_Note_Returns_400()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");
        var hugeNote = new string('x', 501);

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20, note = hugeNote });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-note-too-long");
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

    private async Task<(string clientId, string requestId)> SeedRequestAsync()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package"
        }, default);
        return (clientId, created.Id);
    }
}
