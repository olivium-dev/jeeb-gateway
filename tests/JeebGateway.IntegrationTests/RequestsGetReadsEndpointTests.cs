using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Contract + regression coverage for the ADDITIVE read endpoints on
/// RequestsController (Stream B3, branch feat/requests-get-reads):
///   * GET /requests             → 200 owner-scoped list
///   * GET /requests/{requestId} → 200 / 404 / authz
///
/// These tests are the PR gate: they FAIL the build if the new GETs
/// regress (wrong status code, cross-client leak, lost authz) AND assert
/// the pre-existing POST/DELETE contract is untouched (additive proof).
///
/// The reads come ONLY from the existing in-memory store via the existing
/// IRequestsStore abstraction — no new datastore is introduced.
/// </summary>
public class RequestsGetReadsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestsGetReadsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------
    // GET /requests (list) — 200, owner-scoped
    // ---------------------------------------------------------------

    [Fact]
    public async Task List_Returns_200_With_Only_Callers_Own_Requests()
    {
        var alice = ClientFor("get-list-alice");
        var bob = ClientFor("get-list-bob");

        var a1 = await CreateRequest(alice, "alice req 1");
        var a2 = await CreateRequest(alice, "alice req 2");
        await CreateRequest(bob, "bob req 1");

        var resp = await alice.GetAsync("/requests");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await resp.Content.ReadFromJsonAsync<List<RequestDto>>();
        list.Should().NotBeNull();
        list!.Select(r => r.Id).Should().BeEquivalentTo(new[] { a1, a2 });
        list.Should().OnlyContain(r => r.ClientId == "get-list-alice",
            "the list must be scoped to the caller — no cross-client rows");
    }

    [Fact]
    public async Task List_Returns_200_And_Empty_Array_For_Client_With_No_Requests()
    {
        var fresh = ClientFor("get-list-empty");

        var resp = await fresh.GetAsync("/requests");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await resp.Content.ReadFromJsonAsync<List<RequestDto>>();
        list.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task List_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync("/requests");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_With_Wrong_Role_Returns_403()
    {
        var jeeber = _factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", "get-list-jeeber");
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");

        var resp = await jeeber.GetAsync("/requests");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------
    // GET /requests/{requestId} (read-by-id) — 200 / 404 / authz
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetById_Returns_200_With_The_Request_For_Owner()
    {
        var client = ClientFor("get-byid-owner");
        var id = await CreateRequest(client, "read me back");

        var resp = await client.GetAsync($"/requests/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(id);
        dto.ClientId.Should().Be("get-byid-owner");
        dto.Status.Should().Be("pending");
        dto.Description.Should().Be("read me back");
    }

    [Fact]
    public async Task GetById_Returns_404_For_Unknown_Id()
    {
        var client = ClientFor("get-byid-unknown");

        var resp = await client.GetAsync($"/requests/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_Returns_404_For_Another_Clients_Request()
    {
        var owner = ClientFor("get-byid-real-owner");
        var intruder = ClientFor("get-byid-intruder");

        var id = await CreateRequest(owner, "not yours");

        // Ownership masking: a foreign id is reported as 404 (not 403) so a
        // Client cannot probe for the existence of another Client's ids.
        var resp = await intruder.GetAsync($"/requests/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync($"/requests/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_With_Wrong_Role_Returns_403()
    {
        var jeeber = _factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", "get-byid-jeeber");
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");

        var resp = await jeeber.GetAsync($"/requests/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------
    // ADDITIVE PROOF — pre-existing POST/DELETE contract is untouched
    // ---------------------------------------------------------------

    [Fact]
    public async Task Existing_Post_Still_Returns_201_Created_With_Location()
    {
        var client = ClientFor("additive-post-unchanged");

        var resp = await client.PostAsJsonAsync("/requests", ValidPayload("still 201"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull("POST must still emit the Created location header");
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        dto!.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Existing_Delete_Still_Returns_204_NoContent()
    {
        var client = ClientFor("additive-delete-unchanged");
        var id = await CreateRequest(client, "cancel me");

        var resp = await client.DeleteAsync($"/requests/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Existing_Delete_Unknown_Id_Still_Returns_404()
    {
        var client = ClientFor("additive-delete-404");

        var resp = await client.DeleteAsync($"/requests/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static async Task<string> CreateRequest(HttpClient client, string description)
    {
        var resp = await client.PostAsJsonAsync("/requests", ValidPayload(description));
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "test setup expected creation to succeed");
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        return dto!.Id;
    }

    private static object ValidPayload(string description) => new
    {
        description,
        tierId = "flash",
        pickupLocation = new { lat = 24.7136, lng = 46.6753 },
        dropoffLocation = new { lat = 24.6309, lng = 46.7194 }
    };

    private sealed record RequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        DateTimeOffset CreatedAt);
}
