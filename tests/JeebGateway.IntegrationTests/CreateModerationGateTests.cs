using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-63 (S05 N1 / A1.1): the gateway-owned create-time prohibited-items
/// moderation gate on POST /requests, exercised with
/// <c>FeatureFlags:CreateModeration:Enabled=true</c>.
///
///   * N1  — a block-severity item ("arak") hard-rejects with 409
///           prohibited_item_blocked; an ack must NOT override it.
///   * A1.1 — a warn-severity item ("kitchen knife") soft-rejects with 409
///           prohibited_item_requires_ack until the caller acknowledges the
///           current lexicon version, then the create is allowed (A1.3).
///   * H3  — a clean description is unaffected (still 201).
///
/// The lexicon is gateway-owned (N11) and auto-seeded by DefaultLexiconSeeder
/// when the flag is on, so "arak"/"kitchen knife" match without an admin step.
/// Each test uses a per-test X-User-Id so the shared in-memory ack ledger does
/// not bleed across cases.
/// </summary>
public class CreateModerationGateTests : IClassFixture<CreateModerationGateTests.ModerationOnFactory>
{
    private readonly ModerationOnFactory _factory;

    public CreateModerationGateTests(ModerationOnFactory factory)
    {
        _factory = factory;
    }

    public sealed class ModerationOnFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:CreateModeration:Enabled", "true");
        }
    }

    [Fact]
    public async Task N1_Block_Severity_Item_Hard_Rejects_With_409_Blocked()
    {
        var client = ClientFor("s05-n1-arak");

        var resp = await client.PostAsJsonAsync("/requests", OrderBody("I need a bottle of arak from the shop"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ModerationProblem>();
        problem!.Reason.Should().Be("prohibited_item_blocked");
        problem.Type.Should().Be("https://jeeb.dev/errors/prohibited-item-blocked");
        problem.Matches.Should().Contain(m => m.Severity == "block");
    }

    [Fact]
    public async Task N1_Block_Item_Is_Not_Overridden_By_Ack()
    {
        var client = ClientFor("s05-n1-arak-acked");

        // Acknowledge the current lexicon version first.
        await AcknowledgeCurrentVersion(client);

        var resp = await client.PostAsJsonAsync("/requests", OrderBody("a bottle of arak please"));

        // Block stays a hard reject even with an ack on file (AC7).
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ModerationProblem>();
        problem!.Reason.Should().Be("prohibited_item_blocked");
    }

    [Fact]
    public async Task A1_1_Warn_Item_PreAck_Returns_409_Requires_Ack()
    {
        var client = ClientFor("s05-a11-knife-preack");

        var resp = await client.PostAsJsonAsync("/requests", OrderBody("a kitchen knife"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ModerationProblem>();
        problem!.Reason.Should().Be("prohibited_item_requires_ack");
        problem.Type.Should().Be("https://jeeb.dev/errors/prohibited-item-requires-ack");
        problem.Matches.Should().Contain(m => m.Severity == "warn");
    }

    [Fact]
    public async Task A1_3_Warn_Item_PostAck_Is_Allowed_201()
    {
        var client = ClientFor("s05-a13-knife-postack");

        await AcknowledgeCurrentVersion(client);

        var resp = await client.PostAsJsonAsync("/requests", OrderBody("a kitchen knife"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task H3_Clean_Description_Still_Creates_201_With_Gate_On()
    {
        var client = ClientFor("s05-h3-clean");

        var resp = await client.PostAsJsonAsync("/requests",
            OrderBody("I need two manakish and a bottle of water from the bakery"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ----------------------------------------------------------------- helpers

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static object OrderBody(string description) => new
    {
        description,
        tierId = "flash",
        pickupLocation = new { lat = 33.88, lng = 35.50 },
        dropoffLocation = new { lat = 33.89, lng = 35.51 }
    };

    private async Task AcknowledgeCurrentVersion(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<ProhibitedListDto>("/prohibited-items");
        list.Should().NotBeNull();
        var ack = await client.PostAsJsonAsync("/prohibited-items/acknowledge", new { version = list!.Version });
        ack.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record ModerationProblem(
        string Type,
        string Reason,
        IReadOnlyList<ModerationMatch> Matches);

    private sealed record ModerationMatch(string Keyword, string Category, string Severity);

    private sealed record ProhibitedListDto(string Version);
}
