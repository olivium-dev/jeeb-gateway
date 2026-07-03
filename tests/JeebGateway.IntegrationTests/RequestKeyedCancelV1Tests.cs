using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// fix/offer-visibility P2 (Cycle-2 mobile lane contract gap) — the V1 request-keyed
/// cancel. The mobile client cancels a request PRE-ACCEPT keyed by <c>requestId</c>; the
/// V1 surface previously had no such route (only the frozen legacy
/// <c>DELETE /requests/{id}</c> and <c>POST /deliveries/{id}/cancel</c> existed, forcing
/// the client to resolve a delivery id first). <c>DELETE /v1/requests/{id}</c> and
/// <c>POST /v1/requests/{id}/cancel</c> are now additional route templates on the SAME
/// canonical <c>DeliveriesController.Cancel</c> action (deliveryId == requestId by
/// construction), so they inherit the PR-G2 canonical phase sets, counterparty push, and
/// best-effort upstream propagation unchanged.
/// </summary>
public class RequestKeyedCancelV1Tests
{
    [Fact]
    public async Task DeleteV1Request_PreAccept_ByOwner_CancelsImmediately()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (clientId, requestId) = await SeedRequestAsync(factory);

        // Bare DELETE, no JSON body — must bind an absent body, never 400.
        var resp = await Client(factory, clientId, "customer")
            .DeleteAsync($"/v1/requests/{requestId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        payload!.DeliveryId.Should().Be(requestId);
        payload.Status.Should().Be(RequestStatus.Cancelled,
            "a pre-accept owner cancel commits terminally with no admin approval");
        payload.PendingApproval.Should().BeFalse();

        // The row is terminal in the store (frees the BR-9 slot).
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var row = await store.GetAsync(requestId, CancellationToken.None);
        row!.Status.Should().Be(RequestStatus.Cancelled);
    }

    [Fact]
    public async Task PostV1RequestCancel_Alias_ServesTheSameCanonicalCancel()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (clientId, requestId) = await SeedRequestAsync(factory);

        var resp = await Client(factory, clientId, "customer")
            .PostAsJsonAsync($"/v1/requests/{requestId}/cancel", new { reason = "changed my mind" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        payload!.Status.Should().Be(RequestStatus.Cancelled);
        payload.Reason.Should().Be("changed my mind");
    }

    [Fact]
    public async Task DeleteV1Request_NonParty_Is403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await Client(factory, $"stranger-{Guid.NewGuid():N}", "customer")
            .DeleteAsync($"/v1/requests/{requestId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-a-party");
    }

    [Fact]
    public async Task DeleteV1Request_Unknown_Is404()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await Client(factory, $"client-{Guid.NewGuid():N}", "customer")
            .DeleteAsync($"/v1/requests/{Guid.NewGuid():N}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteV1Request_AlreadyTerminal_Is409NotCancellable()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var http = Client(factory, clientId, "customer");

        (await http.DeleteAsync($"/v1/requests/{requestId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second cancel: the row is terminal → canonical 409 not-cancellable.
        var resp = await http.DeleteAsync($"/v1/requests/{requestId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-cancellable");
    }

    [Fact]
    public async Task LegacyDeliveriesCancelRoute_IsUnchanged()
    {
        // Guard: adding the V1 templates must not disturb the existing route.
        using var factory = new WebApplicationFactory<Program>();
        var (clientId, requestId) = await SeedRequestAsync(factory);

        var resp = await Client(factory, clientId, "customer")
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        payload!.Status.Should().Be(RequestStatus.Cancelled);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static async Task<(string clientId, string requestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "request-keyed cancel parcel",
        }, default);
        return (clientId, created.Id);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }
}
