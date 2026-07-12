using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.ProhibitedItems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-212 (E17): the gateway-owned create-time prohibited-items moderation gate
/// now runs on the V1 create route <c>POST /v1/requests</c> — the route the mobile
/// app actually uses. Before this fix the gate lived only on the legacy
/// <c>POST /requests</c> path, so a prohibited item posted through V1 slipped through
/// unblocked. These tests assert the V1 path enforces the SAME semantics as the legacy
/// path via the shared <c>CreateModerationEvaluator</c>:
///
///   * block-severity ("arak")        → 409 prohibited_item_blocked
///   * warn-severity  ("kitchen knife") pre-ack → 409 prohibited_item_requires_ack
///   * empty/unloadable lexicon        → 503 fail-closed (never silently allowed)
///   * clean description               → 201 Created (gate is inert on clean text)
/// </summary>
public class V1CreateModerationGateTests : IClassFixture<V1CreateModerationGateTests.ModerationOnFactory>
{
    private readonly ModerationOnFactory _factory;

    public V1CreateModerationGateTests(ModerationOnFactory factory)
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
    public async Task Block_Severity_Item_On_V1_Route_Hard_Rejects_With_409_Blocked()
    {
        var client = ClientFor("jebv4212-v1-arak");

        var resp = await client.PostAsJsonAsync("/v1/requests", OrderBody("I need a bottle of arak from the shop"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "a block-severity prohibited item must be rejected on the V1 create route too");
        var problem = await resp.Content.ReadFromJsonAsync<ModerationProblem>();
        problem!.Reason.Should().Be("prohibited_item_blocked");
        problem.Type.Should().Be("https://jeeb.dev/errors/prohibited-item-blocked");
        problem.Matches.Should().Contain(m => m.Severity == "block");
    }

    [Fact]
    public async Task Warn_Severity_Item_On_V1_Route_PreAck_Returns_409_Requires_Ack()
    {
        var client = ClientFor("jebv4212-v1-knife-preack");

        var resp = await client.PostAsJsonAsync("/v1/requests", OrderBody("a kitchen knife"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ModerationProblem>();
        problem!.Reason.Should().Be("prohibited_item_requires_ack");
        problem.Type.Should().Be("https://jeeb.dev/errors/prohibited-item-requires-ack");
        problem.Matches.Should().Contain(m => m.Severity == "warn");
    }

    [Fact]
    public async Task Clean_Description_On_V1_Route_Still_Creates_201_With_Gate_On()
    {
        var client = ClientFor("jebv4212-v1-clean");

        var resp = await client.PostAsJsonAsync("/v1/requests",
            OrderBody("I need two manakish and a bottle of water from the bakery"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "the gate is inert against clean text — the V1 green path must stay 201");
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

    private sealed record ModerationProblem(
        string Type,
        string Reason,
        IReadOnlyList<ModerationMatch> Matches);

    private sealed record ModerationMatch(string Keyword, string Category, string Severity);
}

/// <summary>
/// JEBV4-212: the V1 create route must FAIL CLOSED (503) — never silently allow the
/// request through — when the moderation gate is enabled but the lexicon cannot be
/// loaded (0 active items). Mirrors the legacy <c>CreateModerationFailClosedTests</c>
/// against <c>POST /v1/requests</c>.
/// </summary>
public class V1CreateModerationFailClosedTests
    : IClassFixture<V1CreateModerationFailClosedTests.EmptyLexiconFactory>
{
    private readonly EmptyLexiconFactory _factory;

    public V1CreateModerationFailClosedTests(EmptyLexiconFactory factory)
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

                services.AddSingleton<IProhibitedItemsStore, AlwaysEmptyProhibitedItemsStore>();
            });
        }
    }

    [Fact]
    public async Task EmptyLexicon_OnV1Route_WithModerationEnabled_Returns503()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "jebv4212-v1-fail-closed");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.PostAsJsonAsync("/v1/requests", new
        {
            description = "two bottles of water and some bread",
            tierId = "flash",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            because: "the V1 gate must fail closed when the lexicon is empty (JEBV4-212 / FT-04)");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Moderation service temporarily unavailable");
    }

    /// <summary>Stub store that always reports 0 active items (failed lexicon load).</summary>
    private sealed class AlwaysEmptyProhibitedItemsStore : IProhibitedItemsStore
    {
        public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProhibitedItem>>(Array.Empty<ProhibitedItem>());

        public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new ProhibitedItemsPage { Items = Array.Empty<ProhibitedItem>(), Total = 0 });

        public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
            => Task.FromResult<ProhibitedItem?>(null);

        public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct)
            => Task.FromResult(new ProhibitedItem
            {
                Id = Guid.NewGuid().ToString(),
                Name = input.Name,
                Category = input.Category,
                Description = input.Description,
                Severity = input.Severity,
                Active = false,
                CreatedBy = adminUserId,
                UpdatedBy = adminUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        public Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct)
            => Task.FromResult<ProhibitedItem?>(null);

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserAcknowledgment?>(null);

        public Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct)
            => Task.FromResult(new UserAcknowledgment
            {
                UserId = userId,
                Version = version,
                AcknowledgedAt = DateTimeOffset.UtcNow
            });
    }
}
