using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// sprint-009 Lane E — the jeeber "my-offers" flat branch:
/// <c>GET /v1/offers?jeeberId=&lt;me&gt;</c>. Self-scoped: the jeeberId MUST equal the
/// caller's own id (else 403), and the happy path delegates to
/// <see cref="IOfferServiceClient.ListOffersForJeeberAsync"/> and maps each returned
/// offer (cents → dollars) into the <c>{ items: [...] }</c> envelope the mobile parses.
/// </summary>
public class JeeberMyOffersFlatTests
{
    [Fact]
    public async Task MyOffers_SelfScoped_HappyPath_ReturnsMappedItems()
    {
        var fake = new FakeOfferServiceClient
        {
            Offers = new List<JeeberFeedOffer>
            {
                new() { OfferId = "o-1", RequestId = "r-1", Status = "submitted", FeeCents = 1250, EtaMinutes = 20, Note = "fast", CreatedAt = DateTimeOffset.UnixEpoch },
                new() { OfferId = "o-2", RequestId = "r-2", Status = "edited", FeeCents = 900, EtaMinutes = 15, Note = null, CreatedAt = DateTimeOffset.UnixEpoch },
            }
        };
        using var factory = NewFactory(fake);

        var resp = await Jeeber(factory, "jeeber-me").GetAsync("/v1/offers?jeeberId=jeeber-me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.LastJeeberId.Should().Be("jeeber-me", "the caller's own id is forwarded to offer-service");

        var payload = await resp.Content.ReadFromJsonAsync<ItemsEnvelope>();
        payload!.Items.Should().HaveCount(2);

        var first = payload.Items.Single(i => i.Id == "o-1");
        first.RequestId.Should().Be("r-1");
        first.JeeberId.Should().Be("jeeber-me");
        first.Status.Should().Be("submitted");
        first.Fee.Should().Be(12.50m, "fee_cents (1250) maps to dollars");
        first.EtaMinutes.Should().Be(20);
    }

    [Fact]
    public async Task MyOffers_ForAnotherJeeber_Is403_SelfScope()
    {
        var fake = new FakeOfferServiceClient { Offers = new List<JeeberFeedOffer>() };
        using var factory = NewFactory(fake);

        // jeeber-a asks for jeeber-b's offers → 403, offer-service NEVER consulted.
        var resp = await Jeeber(factory, "jeeber-a").GetAsync("/v1/offers?jeeberId=jeeber-b");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offers-not-self-scoped");
        fake.CallCount.Should().Be(0, "self-scope fails before any upstream call");
    }

    [Fact]
    public async Task MyOffers_UpstreamDegrades_ReturnsEmptyItems_Not5xx()
    {
        // ListOffersForJeeberAsync degrades to empty on any upstream blip; the branch
        // surfaces that as an empty items list, never a 5xx.
        var fake = new FakeOfferServiceClient { Offers = new List<JeeberFeedOffer>() };
        using var factory = NewFactory(fake);

        var resp = await Jeeber(factory, "jeeber-empty").GetAsync("/v1/offers?jeeberId=jeeber-empty");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<ItemsEnvelope>();
        payload!.Items.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(IOfferServiceClient fake)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                });
            });

    private static HttpClient Jeeber(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private sealed record ItemsEnvelope(List<OfferItem> Items);
    private sealed record OfferItem(string Id, string RequestId, string JeeberId, string Status, decimal Fee, int EtaMinutes, string? Note);

    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        public required IReadOnlyList<JeeberFeedOffer> Offers { get; init; }
        public int CallCount { get; private set; }
        public string? LastJeeberId { get; private set; }

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
        {
            CallCount++;
            LastJeeberId = jeeberId;
            return Task.FromResult(Offers);
        }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
