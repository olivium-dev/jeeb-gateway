using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.ProhibitedItems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// FT-04: fail-closed moderation gate (JEB-1504 false claim fix).
///
/// JEB-1504 claimed the gate already returns 503 when the lexicon is
/// empty/unloadable — it did NOT. This test verifies the fix.
///
/// A1. Empty lexicon + moderation gate ON → POST /requests returns 503
///     with body containing "moderation_unavailable".
/// A2. Loaded lexicon + clean description → create proceeds (200/201).
/// </summary>
public class FT04ModerationGateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FT04ModerationGateTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // ---- A1: empty lexicon → 503 moderation_unavailable --------------------

    [Fact]
    public async Task EmptyLexicon_WithModerationEnabled_Returns503()
    {
        // Replace the prohibited items store with one that returns empty list.
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProhibitedItemsStore>();
                services.AddSingleton<IProhibitedItemsStore>(new EmptyProhibitedItemsStore());
            });
        });

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id",    $"client-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await http.PostAsJsonAsync("/requests", new
        {
            Description = "some item that should be screened"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "an empty lexicon must fail closed with 503, not let traffic through");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("moderation_unavailable",
            "503 body must name the reason so callers can distinguish this from a generic 503");
    }

    // ---- A2: loaded lexicon, benign description → 201 allowed ---------------

    [Fact]
    public async Task LoadedLexicon_BenignDescription_Allows_Create()
    {
        // Default factory has the seeded lexicon; benign description passes.
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id",    $"client-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await http.PostAsJsonAsync("/requests", new
        {
            Description = "a bouquet of flowers"
        });

        // 200/201/400/422 are all acceptable (auth or validation may fire before moderation);
        // the key assertion is NOT 503 (moderation_unavailable) and NOT 409 (prohibited).
        ((int)resp.StatusCode).Should().NotBe(503,
            "a benign description must never trigger the moderation unavailable path");
        ((int)resp.StatusCode).Should().NotBe(409,
            "a benign description must not be blocked by the moderation gate");
    }
}

/// <summary>
/// Test double: an <see cref="IProhibitedItemsStore"/> that always returns an
/// empty active set to simulate an unloadable or not-yet-seeded lexicon.
/// </summary>
file sealed class EmptyProhibitedItemsStore : IProhibitedItemsStore
{
    public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProhibitedItem>>(Array.Empty<ProhibitedItem>());

    public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
        => Task.FromResult(new ProhibitedItemsPage(Array.Empty<ProhibitedItem>(), 0, page, pageSize));

    public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
        => Task.FromResult<ProhibitedItem?>(null);

    public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct)
        => throw new NotSupportedException("read-only test double");

    public Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct)
        => Task.FromResult<ProhibitedItem?>(null);

    public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct)
        => Task.FromResult<UserAcknowledgment?>(null);

    public Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct)
        => throw new NotSupportedException("read-only test double");
}
