using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.ProhibitedItems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1504: fail-closed prohibited-items moderation gate.
/// When moderation is enabled but the lexicon cannot be loaded (0 active
/// items in the store), the gateway MUST return 503 Service Unavailable
/// rather than silently allowing the request through (fail-open).
/// </summary>
public class CreateModerationFailClosedTests
    : IClassFixture<CreateModerationFailClosedTests.EmptyLexiconFactory>
{
    private readonly EmptyLexiconFactory _factory;

    public CreateModerationFailClosedTests(EmptyLexiconFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Factory that enables the moderation gate AND replaces the
    /// <see cref="IProhibitedItemsStore"/> with an always-empty stub so
    /// the lexicon is never populated, simulating a seeder failure or
    /// an unreachable backing store on startup.
    /// </summary>
    public sealed class EmptyLexiconFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:CreateModeration:Enabled", "true");

            builder.ConfigureServices(services =>
            {
                // Remove the real IProhibitedItemsStore registration so the
                // empty stub takes over. The DefaultLexiconSeeder will try to
                // call ListActiveAsync at startup and find 0 items but will
                // not seed anything because CreateAsync on the stub is a no-op.
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IProhibitedItemsStore));
                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddSingleton<IProhibitedItemsStore, AlwaysEmptyProhibitedItemsStore>();
            });
        }
    }

    [Fact]
    public async Task EmptyLexicon_WithModerationEnabled_Returns503()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "jeb1504-fail-closed-user");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "I need some groceries",
            tierId = "flash",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            because: "moderation gate must fail closed when lexicon is empty (JEB-1504)");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Moderation service temporarily unavailable");
    }

    [Fact]
    public async Task EmptyLexicon_EvenForCleanDescription_Returns503()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "jeb1504-clean-desc-user");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        // A completely clean description still triggers 503 because the
        // problem is the lexicon being unloaded, not the content.
        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "two bottles of water and some bread",
            tierId = "flash",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            because: "gate must fail closed regardless of description content when lexicon is empty");
    }

    /// <summary>
    /// Stub store that always returns 0 active items, simulating a failed
    /// lexicon load. All mutation operations are no-ops so the
    /// <see cref="DefaultLexiconSeeder"/> does not accidentally populate it.
    /// </summary>
    private sealed class AlwaysEmptyProhibitedItemsStore : IProhibitedItemsStore
    {
        public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProhibitedItem>>(Array.Empty<ProhibitedItem>());

        public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new ProhibitedItemsPage { Items = Array.Empty<ProhibitedItem>(), Total = 0 });

        public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
            => Task.FromResult<ProhibitedItem?>(null);

        public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct)
        {
            // No-op: return a dummy item so the seeder doesn't crash.
            var item = new ProhibitedItem
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
            };
            return Task.FromResult(item);
        }

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
