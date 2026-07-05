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
    // PR-G1 — canonical status normalization + IsListableActive filter
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Deliveries_CanonicalNormalization_PickedRow_ListsAsPicked_NotPickedUp()
    {
        // A row persisted under the LEGACY vocabulary (picked_up) must surface on the
        // list as the CANONICAL token 'Picked'.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.PickedUp); // legacy "picked_up"

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");

        var item = page!.Items.Single(i => i.Id == requestId);
        item.Status.Should().Be(CanonicalDeliveryStatus.Picked, "legacy picked_up dual-reads to canonical Picked");
        item.Status.Should().NotBe(RequestStatus.PickedUp);
    }

    [Fact]
    public async Task Deliveries_CanonicalNormalization_AlreadyCanonicalPickedRow_ListsAsPicked_Consistently()
    {
        // The inverse consistency: a row already stamped with the CANONICAL token
        // 'Picked' must ALSO list as 'Picked' (idempotent dual-read), so both vocabularies
        // converge on the same list value.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, CanonicalDeliveryStatus.Picked); // canonical "Picked"

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");

        page!.Items.Single(i => i.Id == requestId).Status.Should().Be(CanonicalDeliveryStatus.Picked);
    }

    [Fact]
    public async Task Deliveries_ExcludesTerminalRow_But_ClientRequestsHistory_IncludesIt()
    {
        // A terminal (delivered → canonical Done) row drops OUT of the active Jobs/Deliveries
        // surface but stays IN the client's /v1/requests history (canonical-status'd).
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.Delivered); // legacy terminal → canonical Done

        var deliveries = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");
        deliveries!.Items.Should().NotContain(i => i.Id == requestId,
            "a terminal (Done) delivery must not occupy the active Jobs list");

        // Client history is UNFILTERED — the terminal row is still present, canonical-status'd.
        var history = await ClientActor(factory, Client)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=client");
        var historyItem = history!.Items.Single(i => i.Id == requestId);
        historyItem.Status.Should().Be(CanonicalDeliveryStatus.Done,
            "the client order history retains terminal rows, surfaced as the canonical Done token");
    }

    [Fact]
    public async Task RequestsRoleJeeber_ExcludesTerminalRow()
    {
        // The role=jeeber branch of /v1/requests is the driver's active-jobs surface and
        // is filtered like /v1/deliveries — a cancelled (terminal) row drops out.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.Cancelled);

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=jeeber");

        page!.Items.Should().NotContain(i => i.Id == requestId);
    }

    // ---------------------------------------------------------------------
    // FIX-2 — jeeber Completed tab: ?status= bucket on /v1/deliveries
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("delivered")] // the shipped mobile DioOrderRepository token
    [InlineData("completed")]
    [InlineData("done")]
    public async Task Deliveries_StatusCompleted_AsAssignedJeeber_ReturnsTheDoneRow(string token)
    {
        // The regression: a jeeber's completed (Done) delivery was dropped by the
        // unconditional active-only filter, so the Completed tab was always empty.
        // With ?status=<completed-alias> the Done row now surfaces to the assigned jeeber.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.Delivered); // legacy terminal → canonical Done

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>($"/v1/deliveries?status={token}");

        var item = page!.Items.Should().ContainSingle(i => i.Id == requestId,
            "the completed bucket must surface the jeeber's Done delivery").Subject;
        item.Status.Should().Be(CanonicalDeliveryStatus.Done);
        item.JeeberId.Should().Be(Jeeber);
        page.TotalCount.Should().Be(1, "totalCount reflects the completed bucket");
    }

    [Fact]
    public async Task Deliveries_StatusCompleted_ExcludesInFlightRow()
    {
        // The Completed bucket must NOT leak an active (in-flight) delivery.
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.PickedUp); // in-flight

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries?status=delivered");

        page!.Items.Should().NotContain(i => i.Id == requestId,
            "an in-flight delivery must not appear in the Completed bucket");
    }

    [Fact]
    public async Task Deliveries_DefaultActiveBucket_StillExcludesDoneRow_NoRegression()
    {
        // The default (no ?status=) path is byte-identical to before: a Done row stays
        // OUT of the active Jobs list (so the active list + BR-10 slot accounting are untouched).
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.Delivered); // canonical Done

        var noParam = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");
        noParam!.Items.Should().NotContain(i => i.Id == requestId,
            "default active bucket still excludes terminal Done rows");

        var explicitActive = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries?status=active");
        explicitActive!.Items.Should().NotContain(i => i.Id == requestId,
            "explicit status=active is identical to the default");
    }

    [Fact]
    public async Task Deliveries_StatusCancelled_ReturnsCancelledRow_NotInActiveOrCompleted()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requestId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, requestId, RequestStatus.Cancelled); // canonical Cancelled

        var cancelled = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries?status=cancelled");
        cancelled!.Items.Should().ContainSingle(i => i.Id == requestId);
        cancelled.Items.Single(i => i.Id == requestId).Status.Should().Be(CanonicalDeliveryStatus.Cancelled);

        var completed = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries?status=delivered");
        completed!.Items.Should().NotContain(i => i.Id == requestId,
            "a cancelled row must not appear in the Completed (Done) bucket");
    }

    [Fact]
    public async Task Deliveries_UnknownStatusToken_FallsBackToActive_NeverLeaksTerminal()
    {
        // A malformed/unknown token must degrade to the safe active default — it must never
        // spill terminal rows into the surface.
        using var factory = new WebApplicationFactory<Program>();
        var doneId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, doneId, RequestStatus.Delivered); // Done (terminal)
        var activeId = await SeedAcceptedDeliveryAsync(factory, Client, Jeeber);
        await SetStatusAsync(factory, activeId, RequestStatus.PickedUp); // in-flight

        var page = await JeeberActor(factory, Jeeber)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries?status=bananas");

        page!.Items.Should().Contain(i => i.Id == activeId, "unknown token falls back to active");
        page.Items.Should().NotContain(i => i.Id == doneId, "unknown token must not leak terminal rows");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static async Task SetStatusAsync(
        WebApplicationFactory<Program> factory, string requestId, string status)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.SetStatusAsync(requestId, status, CancellationToken.None))
            .Should().BeTrue($"setup: move seeded row to {status}");
    }

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
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("jeeberId")] public string? JeeberId { get; set; }
        [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }
    }
}
