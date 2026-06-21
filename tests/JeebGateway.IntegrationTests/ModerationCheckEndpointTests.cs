using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.ProhibitedItems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-06 (RAT-04 / ACCT-03 / ADM-03): the synchronous content-moderation check
/// <c>POST /moderation/jeeb/check</c>. It reuses the create-gate lexicon scan to
/// moderate arbitrary text (display name, request description) and returns an
/// allow / warn / block verdict that mirrors the create-gate outcome exactly, plus
/// the current lexicon version so a warn caller can drive the version-pinned ack flow.
///
/// The gate-on factory auto-seeds "arak" (block) and "kitchen knife" (warn) via
/// DefaultLexiconSeeder, so no admin step is needed to exercise the verdicts.
/// </summary>
public class ModerationCheckEndpointTests : IClassFixture<ModerationCheckEndpointTests.ModerationOnFactory>
{
    private readonly ModerationOnFactory _factory;

    public ModerationCheckEndpointTests(ModerationOnFactory factory)
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
    public async Task Check_Block_Item_Returns_Block_Verdict_With_Reason_And_Matches()
    {
        var client = ParticipantClient("wsc-block");

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check", new { text = "a bottle of arak please" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CheckDto>();
        body!.Decision.Should().Be("block");
        body.Reason.Should().Be("prohibited_item_blocked");
        body.Version.Should().NotBeNullOrWhiteSpace();
        body.Matches.Should().Contain(m => m.Severity == "block");
    }

    [Fact]
    public async Task Check_Warn_Item_Returns_Warn_Verdict_And_Current_Version()
    {
        var client = ParticipantClient("wsc-warn");

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check", new { text = "I need a kitchen knife" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CheckDto>();
        body!.Decision.Should().Be("warn");
        body.Reason.Should().Be("prohibited_item_requires_ack");
        body.Matches.Should().Contain(m => m.Severity == "warn");

        // The returned version drives the version-pinned acknowledge flow: it must
        // match what GET /prohibited-items reports so a warn caller can acknowledge it.
        var list = await client.GetFromJsonAsync<ListDto>("/prohibited-items");
        body.Version.Should().Be(list!.Version);
    }

    [Fact]
    public async Task Check_Clean_Text_Returns_Allow_With_No_Reason()
    {
        var client = ParticipantClient("wsc-allow");

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check",
            new { text = "Layla" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CheckDto>();
        body!.Decision.Should().Be("allow");
        body.Reason.Should().BeNull();
        body.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient(); // no identity headers

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check", new { text = "arak" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Check_With_Blank_Text_Returns_400()
    {
        var client = ParticipantClient("wsc-blank");

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check", new { text = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient ParticipantClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private sealed record CheckDto(
        string Decision,
        string Version,
        string? Reason,
        IReadOnlyList<MatchDto> Matches);

    private sealed record MatchDto(string Keyword, string Category, string Severity);

    private sealed record ListDto(string Version);
}

/// <summary>
/// WS-06 fail-closed: the standalone content-check MUST 503 (not allow) when the
/// lexicon is empty/unloadable, identical to the request-create gate (JEB-1504).
/// Re-uses the always-empty store stub so the lexicon never populates.
/// </summary>
public class ModerationCheckFailClosedTests
    : IClassFixture<ModerationCheckFailClosedTests.EmptyLexiconFactory>
{
    private readonly EmptyLexiconFactory _factory;

    public ModerationCheckFailClosedTests(EmptyLexiconFactory factory)
    {
        _factory = factory;
    }

    public sealed class EmptyLexiconFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:CreateModeration:Enabled", "true");

            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IProhibitedItemsStore));
                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddSingleton<IProhibitedItemsStore, EmptyStore>();

                // The DefaultLexiconSeeder IHostedService seeds the default lexicon on startup
                // whenever the store is empty — which an always-empty store always is — so it
                // would call EmptyStore.CreateAsync at boot and crash the host. This test's
                // whole premise is an EMPTY lexicon (fail-closed), so the seeder must not run;
                // drop it so the lexicon genuinely stays empty and the host can boot.
                var seeder = services.SingleOrDefault(
                    d => d.ImplementationType == typeof(JeebGateway.ProhibitedItems.DefaultLexiconSeeder));
                if (seeder is not null)
                    services.Remove(seeder);
            });
        }
    }

    [Fact]
    public async Task Check_EmptyLexicon_Returns_503_Not_Allow()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "wsc-fail-closed");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.PostAsJsonAsync("/moderation/jeeb/check", new { text = "anything at all" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>Always-empty store: ListActive yields nothing so the gate fails closed.</summary>
    private sealed class EmptyStore : IProhibitedItemsStore
    {
        public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProhibitedItem>>(Array.Empty<ProhibitedItem>());

        public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct) =>
            Task.FromResult(new ProhibitedItemsPage { Items = Array.Empty<ProhibitedItem>(), Total = 0 });

        public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult<ProhibitedItem?>(null);

        public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct) =>
            Task.FromResult<ProhibitedItem?>(null);

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct) =>
            Task.FromResult<UserAcknowledgment?>(null);

        public Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
