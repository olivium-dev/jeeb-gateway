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
/// S03 GAP-A regression guard (defect S002-BLOCK-OFFER-CREATE).
///
/// The mobile app submits offers against the <c>/v1/</c>-prefixed template
/// <c>POST /v1/requests/{requestId}/offers</c>. Before S03 only the GET was
/// bound to that template (JeebRequestsController), so the POST returned
/// <b>405 Method Not Allowed (Allow: GET)</b> and Step 3 of the Core Flow
/// could never start. The fix registers the <c>/v1/</c> POST template on the
/// existing <see cref="RequestOffersController.Submit"/> action so it serves
/// byte-identically to the legacy un-prefixed route.
///
/// These tests pin the WRITE verb on the <c>/v1/</c> path (L20: probe the verb
/// the app actually calls, never just GET) and assert:
/// <list type="bullet">
///   <item>happy path → <b>201 Created</b> with an <c>offerId</c> (NOT 405);</item>
///   <item>unknown request → <b>404</b> (route is genuinely bound — a 405 would
///     mean the POST template is still missing);</item>
///   <item>the legacy un-prefixed route still returns 201 (no regression).</item>
/// </list>
/// </summary>
public class V1OfferCreateRouteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public V1OfferCreateRouteTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task V1_Submit_Returns_201_With_OfferId_Not_405()
    {
        var (clientId, requestId) = await SeedRequestAsync();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var jeeberClient = JeeberClient(jeeberId);

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/v1/requests/{requestId}/offers",
            new { fee = 12.5m, etaMinutes = 30, note = "On my way (v1)" });

        // The headline assertion: the /v1/ POST must NOT be 405 anymore.
        resp.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
            "GAP-A: POST /v1/requests/{id}/offers was 405 before S03 (GET-only template)");
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await resp.Content.ReadFromJsonAsync<OfferDto>();
        dto.Should().NotBeNull();
        dto!.Id.Should().NotBeNullOrWhiteSpace("the 201 must carry an offerId");
        dto.RequestId.Should().Be(requestId);
        dto.JeeberId.Should().Be(jeeberId);
        dto.Status.Should().Be("pending");
        dto.Fee.Should().Be(12.5m);
        dto.EtaMinutes.Should().Be(30);

        // Same code path → the realtime fan-out still fires.
        var realtime = _factory.Services.GetRequiredService<InMemoryOfferRealtimeNotifier>();
        realtime.Events.Should().Contain(e =>
            e.OfferId == dto.Id
            && e.RequestId == requestId
            && e.ClientId == clientId
            && e.JeeberId == jeeberId);
    }

    [Fact]
    public async Task V1_Submit_For_Unknown_Request_Returns_404_Not_405()
    {
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/v1/requests/{Guid.NewGuid()}/offers",
            new { fee = 5m, etaMinutes = 20 });

        // A 405 here would mean the POST template is still unbound. A 404 proves
        // the route reached the Submit action and failed on the missing request.
        resp.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task V1_Submit_With_Fee_Below_Floor_Returns_400_Via_Shared_Logic()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/v1/requests/{requestId}/offers",
            new { fee = 0.50m, etaMinutes = 20 });

        // Proves the /v1/ route runs the SAME validation as the legacy route.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-fee-too-low");
    }

    [Fact]
    public async Task Legacy_Unprefixed_Route_Still_Returns_201_No_Regression()
    {
        var (_, requestId) = await SeedRequestAsync();
        var jeeberClient = JeeberClient($"jeeber-{Guid.NewGuid()}");

        var resp = await jeeberClient.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 7m, etaMinutes = 25 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "adding the /v1/ template must not change the legacy route behavior");
    }

    // -----------------------------------------------------------------
    // Helpers (mirror RequestOffersEndpointTests)
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
