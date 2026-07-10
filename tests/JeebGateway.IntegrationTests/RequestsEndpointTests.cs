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
/// BR-9 (T-backend-049 + T-backend-007): the active-request cap is retired.
/// Clients may create more than 3 active delivery requests.
///
/// T-backend-007 added tier + structured location fields as required on
/// the create body — the <see cref="ValidPayload"/> helper builds the
/// minimum valid payload so each test stays focused on request creation
/// rather than the new field-level validators (covered separately in
/// <see cref="DeliveryRequestCreationTests"/>).
///
/// These tests share a single WebApplicationFactory and therefore a
/// single in-memory store across cases. Each test scopes itself by using
/// a unique <c>X-User-Id</c> so the per-client active counts don't bleed
/// between tests.
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

        var resp = await client.PostAsJsonAsync("/requests", ValidPayload(
            description: "Pick up groceries from Carrefour",
            pickupAddress: "Carrefour Mall of Arabia",
            dropoffAddress: "Riyadh, Diplomatic Quarter"));

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

        var resp = await anon.PostAsJsonAsync("/requests", ValidPayload("anonymous"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_Rejects_Blank_Description()
    {
        var client = ClientFor("br9-blank-desc");

        var resp = await client.PostAsJsonAsync("/requests", ValidPayload(description: "   "));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Fourth_Active_Request_Succeeds()
    {
        var client = ClientFor("br9-cap");

        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", ValidPayload($"req {i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"creation {i} should succeed");
        }

        var fourth = await client.PostAsJsonAsync("/requests", ValidPayload("fourth"));
        fourth.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task More_Than_Three_Active_Requests_Are_Allowed_Per_Client()
    {
        var alice = ClientFor("br9-alice");
        var bob = ClientFor("br9-bob");

        for (var i = 0; i < 3; i++)
        {
            (await alice.PostAsJsonAsync("/requests", ValidPayload($"alice {i}")))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var bobResp = await bob.PostAsJsonAsync("/requests", ValidPayload("bob 1"));
        bobResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var aliceFourth = await alice.PostAsJsonAsync("/requests", ValidPayload("alice 4"));
        aliceFourth.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Delivered_Request_Does_Not_Count_Toward_Cap()
    {
        var client = ClientFor("br9-delivered-frees-slot");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", ValidPayload($"req {i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        // Flip the first one to a terminal post-delivered state. Creation should
        // still succeed now that active-request concurrency is unlimited.
        await MoveToStatus(created[0], "delivered");

        var fourth = await client.PostAsJsonAsync("/requests", ValidPayload("now allowed"));
        fourth.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cancelled_Request_Does_Not_Block_Additional_Create()
    {
        var client = ClientFor("br9-cancelled-frees-slot");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", ValidPayload($"req {i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        await MoveToStatus(created[1], "cancelled");

        var replacement = await client.PostAsJsonAsync("/requests", ValidPayload("replacement"));
        replacement.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("matched")]
    [InlineData("accepted")]
    [InlineData("picked_up")]
    [InlineData("heading_off")]
    public async Task Status_Strictly_Before_Delivered_Does_Not_Block_Additional_Create(string activeStatus)
    {
        var client = ClientFor($"br9-active-{activeStatus}");

        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/requests", ValidPayload($"req {i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Created);
            var dto = await ok.Content.ReadFromJsonAsync<RequestDto>();
            created.Add(dto!.Id);
        }

        // Move one request through a still-active state. Additional creates
        // should still succeed because the active-request cap is retired.
        await MoveToStatus(created[0], activeStatus);

        var fourth = await client.PostAsJsonAsync("/requests", ValidPayload("fourth active"));
        fourth.StatusCode.Should().Be(HttpStatusCode.Created);
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

    /// <summary>
    /// Minimum body that satisfies T-backend-007 validation — tier +
    /// pickup/dropoff coordinates. Tests targeting BR-9 don't care about
    /// the values, only that they're present and valid so the request
    /// reaches the store layer.
    /// </summary>
    private static object ValidPayload(
        string description,
        string? pickupAddress = null,
        string? dropoffAddress = null) => new
    {
        description,
        tierId = "flash",
        pickupLocation = new { lat = 24.7136, lng = 46.6753 },
        dropoffLocation = new { lat = 24.6309, lng = 46.7194 },
        pickupAddress,
        dropoffAddress
    };

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
