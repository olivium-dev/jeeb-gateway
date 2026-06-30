using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P0 (device-QA regression) — the jeeber Jobs/Deliveries list was ALWAYS EMPTY.
///
/// <para>ROOT CAUSE: <c>GET /v1/deliveries</c> and the <c>role=jeeber</c> branch of
/// <c>GET /v1/requests</c> sourced rows from <see cref="IRequestsStore.ListForClientAsync"/>
/// (rows WHERE <c>ClientId == caller</c>) and then filtered <c>JeeberId == caller</c>. An
/// accepted delivery's <c>ClientId</c> is the REQUESTING CLIENT (e.g. Nour), never the
/// assigned jeeber (e.g. Karim), so that intersection was empty for every jeeber and the
/// Jobs tab never showed an accepted job.</para>
///
/// <para>FIX: a new store primitive <see cref="IRequestsStore.ListForJeeberAsync"/>
/// (rows WHERE <c>JeeberId == caller</c>) feeds the jeeber-side reads; <c>GET /v1/deliveries</c>
/// unions the caller's jeeber-assigned rows with their own client rows. These tests prove the
/// assigned delivery now surfaces to the jeeber, an unrelated jeeber still sees nothing
/// (no over-sharing), and the client-side read is unregressed.</para>
///
/// The rows are seeded through the REAL <see cref="IRequestsStore"/> exactly as production
/// writes them: create as the client, then <see cref="IRequestsStore.TryAcceptByJeeberAsync"/>
/// stamps the winning <c>JeeberId</c> (the same primitive the flag-off accept path calls).
/// </summary>
public class S03JeeberDeliveryListTests
{
    private const string Client = "client-nour";
    private const string Jeeber = "jeeber-karim";
    private const string OtherJeeber = "jeeber-rana";

    [Fact]
    public async Task Deliveries_AsAssignedJeeber_ReturnsTheActiveDelivery()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");

        page!.Items.Should().ContainSingle(i => i.Id == requestId,
            "the assigned jeeber must see the delivery they were awarded");
        page.Items.Single(i => i.Id == requestId).JeeberId.Should().Be(Jeeber);
    }

    [Fact]
    public async Task Deliveries_AsUnrelatedJeeber_IsEmpty()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);

        var page = await JeeberActor(factory, OtherJeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");

        page!.Items.Should().NotContain(i => i.Id == requestId,
            "a jeeber who was not assigned must never see another jeeber's delivery");
    }

    [Fact]
    public async Task Requests_RoleJeeber_AsAssignedJeeber_ReturnsTheAssignedJob()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=jeeber");

        page!.Items.Should().ContainSingle(i => i.Id == requestId);
    }

    [Fact]
    public async Task Requests_RoleClient_AsOwningClient_StillReturnsOwnRow_NoRegression()
    {
        // Guard: the P0 rewire must not regress the client-side read. The owning client
        // still sees their request via role=client; an unassigned third party does not.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);

        var clientPage = await ClientActor(factory, Client)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=client");
        clientPage!.Items.Should().ContainSingle(i => i.Id == requestId);

        // The jeeber, asking with role=client, owns no client rows → empty (negative).
        var jeeberAsClient = await ClientActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=client");
        jeeberAsClient!.Items.Should().NotContain(i => i.Id == requestId);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static async Task<string> SeedAcceptedDeliveryAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Deliver the parcel to Karim",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 33.5138, Lng = 36.2765 },
            DropoffLocation = new GeoPoint { Lat = 33.52, Lng = 36.28 },
        }, CancellationToken.None);

        // Production flag-off accept path: stamp the winning jeeber onto the row.
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: 2, DateTimeOffset.UtcNow, CancellationToken.None);
        accepted.Should().NotBeNull();
        accepted!.JeeberId.Should().Be(jeeberId);
        return created.Id;
    }

    private static HttpClient JeeberActor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    private sealed class PagedEnvelope
    {
        [JsonPropertyName("items")] public List<Item> Items { get; set; } = new();
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("jeeberId")] public string? JeeberId { get; set; }
        [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }
    }
}
