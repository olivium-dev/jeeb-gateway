using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// sprint-009 Lane E — the flat offer-scoped withdraw alias
/// <c>DELETE /v1/offers/{offerId}</c>. The mobile client withdraws a bid by offer id
/// alone; the alias resolves the <c>offerId → requestId</c> pairing recorded at submit
/// time and reuses the request-scoped withdraw logic verbatim. An offer unknown to this
/// gateway instance resolves to a 404 (phantom offer).
/// </summary>
public class FlatWithdrawAliasTests
{
    [Fact]
    public async Task FlatWithdraw_ResolvesRequestId_AndDeletesTheOffer_204()
    {
        using var factory = new WebApplicationFactory<Program>();

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        // Submit records the offerId → (requestId, jeeberId) pairing in the routing index.
        var submit = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20, note = "bidding" });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        var offer = (await submit.Content.ReadFromJsonAsync<OfferDto>())!;

        // Withdraw by offer id ALONE via the flat alias — the alias resolves requestId itself.
        var resp = await jeeber.DeleteAsync($"/v1/offers/{offer.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task FlatWithdraw_UnknownOffer_Is404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        // Never submitted → not in the routing index → phantom offer → 404.
        var resp = await jeeber.DeleteAsync($"/v1/offers/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<(string clientId, string requestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package",
        }, default);
        return (clientId, created.Id);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }
}
