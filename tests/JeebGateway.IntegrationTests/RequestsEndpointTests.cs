using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BR-9 (T-backend-049): a Client may have at most 3 active (non-delivered)
/// delivery requests. Request creation must return 409 once the cap is hit.
///
/// These tests share a single WebApplicationFactory and therefore a single
/// in-memory store across cases. Each test scopes itself by using a unique
/// <c>X-User-Id</c> so the per-client active counts don't bleed between
/// tests.
/// </summary>
public class RequestsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_Returns_201_With_Id_And_Pending_Status()
    {
        var client = ClientFor("br9-happy-path");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "Pick up groceries from Carrefour",
            pickupAddress = "Carrefour Mall of Arabia",
            dropoffAddress = "Riyadh, Diplomatic Quarter"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<RequestDto>();
        body!.Id.Should().NotBeNullOrWhiteSpace();
        body.ClientId.Should().Be("br9-happy-path");
        body.Status.Should().Be("pending");
        body.Description.Should().Be("Pick up groceries from Carrefour");
    }

    [Fact]
    public async Task Create_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/requests", new
        {
            description = "anonymous"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_Rejects_Blank_Description()
    {
        var client = ClientFor("br9-blank-desc");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "   "
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Fourth_Active_Request_Returns_409_With_BR9_Message()
    {
        var client = ClientFor("br9-cap");

        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", new { description = $"req {i}" });
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"creation {i} should succeed under the cap");
        }

        var blocked = await client.PostAsJsonAsync("/requests", new { description = "fourth" });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await blocked.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Maximum 3 active requests. Complete or cancel an existing request.");
        problem.Status.Should().Be((int)HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Active_Cap_Is_Per_Client()
    {
        var alice = ClientFor("br9-alice");
        var bob = ClientFor("br9-bob");

        for (var i = 0; i < 3; i++)
        {
            (await alice.PostAsJsonAsync("/requests", new { description = $"alice {i}" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Alice is now at the cap; Bob is unaffected.
        var bobResp = await bob.PostAsJsonAsync("/requests", new { description = "bob 1" });
        bobResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var aliceBlocked = await alice.PostAsJsonAsync("/requests", new { description = "alice 4" });
        aliceBlocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delivered_Request_Does_Not_Count_Toward_Cap()
    {
        var client = ClientFor("br9-delivered-frees-slot");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", new { description = $"req {i}" });
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        // Flip the first one to a terminal post-delivered state. This must
        // free a slot — only states strictly before 'delivered' count.
        await MoveToStatus(created[0], "delivered");

        var fourth = await client.PostAsJsonAsync("/requests", new { description = "now allowed" });
        fourth.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cancelled_Request_Does_Not_Count_Toward_Cap()
    {
        var client = ClientFor("br9-cancelled-frees-slot");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", new { description = $"req {i}" });
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        await MoveToStatus(created[1], "cancelled");

        var replacement = await client.PostAsJsonAsync("/requests", new { description = "replacement" });
        replacement.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("matched")]
    [InlineData("accepted")]
    [InlineData("picked_up")]
    [InlineData("heading_off")]
    public async Task Status_Strictly_Before_Delivered_Still_Counts_As_Active(string activeStatus)
    {
        var client = ClientFor($"br9-active-{activeStatus}");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", new { description = $"req {i}" });
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        // Move one request through a still-active state — the cap must
        // still apply because that state is strictly before 'delivered'.
        await MoveToStatus(created[0], activeStatus);

        var blocked = await client.PostAsJsonAsync("/requests", new { description = "should be blocked" });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private async Task MoveToStatus(string requestId, string status)
    {
        // The gateway has no public mutation endpoint yet — the delivery
        // state machine lives in delivery-service. Tests reach into the
        // singleton store directly to advance status.
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var ok = await store.SetStatusAsync(requestId, status, CancellationToken.None);
        ok.Should().BeTrue($"test setup expected to find request {requestId}");
    }

    private sealed record RequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt);
}
