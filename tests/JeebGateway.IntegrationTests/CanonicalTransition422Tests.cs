using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-002 PR-3 (owner-approved 2026-06-04) — CONTRACT-AFFECTING, CANARY-GATED.
///
/// Asserts the gateway's illegal-transition response can be flipped from the
/// legacy HTTP 400 to the canonical HTTP 422 + typed <c>transition_not_allowed</c>
/// body (ADR-002 §4) ONLY when the <c>CanonicalTransition422</c> flag is on.
///
/// The headline guarantee for the canary plan: with the flag OFF (the default in
/// EVERY environment), the live response is byte-for-byte the legacy 400 — so
/// merging/deploying this PR introduces ZERO live behavior change until the CTO
/// flips the flag during the canary.
/// </summary>
public class CanonicalTransition422Tests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CanonicalTransition422Tests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -------- flag OFF (default): legacy 400 is preserved ---------------------

    [Fact]
    public async Task FlagOff_IllegalTransition_Returns_Legacy_400()
    {
        await using var factory = FactoryWith422(enabled: false);
        var seed = await SeedAsync(factory, RequestStatus.Accepted);
        var http = AuthClient(factory, seed.JeeberId);

        // Accepted -> Delivered skips picked_up/heading_off: illegal.
        var resp = await Patch(http, seed.Id, RequestStatus.Delivered);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "with the canary flag OFF the gateway must keep returning the legacy 400");
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/invalid-transition");
    }

    // -------- flag ON (canary): canonical 422 + typed body --------------------

    [Fact]
    public async Task FlagOn_IllegalTransition_Returns_422_TransitionNotAllowed()
    {
        await using var factory = FactoryWith422(enabled: true);
        var seed = await SeedAsync(factory, RequestStatus.Accepted);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await Patch(http, seed.Id, RequestStatus.Delivered);

        ((int)resp.StatusCode).Should().Be(422, "the canary flag is ON");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/transition-not-allowed");
        problem.GetProperty("reason").GetString().Should().Be(DeliverySm.ReasonTransitionNotAllowed);
        problem.GetProperty("status").GetInt32().Should().Be(422);

        // The body speaks the CANONICAL lexicon even though the seeded row holds
        // the legacy 'accepted' token (entry edge ⇒ Ordered) and the requested
        // target 'delivered' ⇒ Done.
        problem.GetProperty("from").GetString().Should().Be(CanonicalDeliveryStatus.Ordered);
        problem.GetProperty("to").GetString().Should().Be(CanonicalDeliveryStatus.Done);
    }

    [Fact]
    public async Task FlagOn_ValidHappyPath_Still_Succeeds()
    {
        // The 422 change must not affect legal transitions — the happy path is
        // untouched whether or not the flag is on.
        await using var factory = FactoryWith422(enabled: true);
        var seed = await SeedAsync(factory, RequestStatus.Accepted);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await Patch(http, seed.Id, RequestStatus.PickedUp);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ----------------------- helpers -----------------------------------------

    private WebApplicationFactory<Program> FactoryWith422(bool enabled) =>
        _factory.WithWebHostBuilder(builder =>
            builder.UseSetting(
                "FeatureFlags:UseUpstream:CanonicalTransition422",
                enabled ? "true" : "false"));

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory, string landing)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
        }, CancellationToken.None);

        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();

        if (landing != RequestStatus.Accepted)
        {
            (await store.SetStatusAsync(created.Id, landing, default)).Should().BeTrue();
        }

        return new Seed(created.Id, clientId, jeeberId);
    }

    private static Task<HttpResponseMessage> Patch(HttpClient http, string deliveryId, string toStatus) =>
        http.PatchAsync($"/deliveries/{deliveryId}/status", JsonContent.Create(new { status = toStatus }));

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
