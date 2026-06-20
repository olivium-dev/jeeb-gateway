using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-06 (ADM-03): admin bulk-import of prohibited items via
/// <c>POST /admin/prohibited-items/bulk-import</c>. Partial success is the
/// contract — a bad or duplicate row is reported per-row and does NOT abort the
/// batch. Validation reuses the single-item create rules (name/category/severity),
/// and the duplicate-name guard mirrors single create's 409 semantics as a
/// per-row "duplicate" outcome.
/// </summary>
public class AdminProhibitedItemsBulkImportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminProhibitedItemsBulkImportTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BulkImport_Creates_All_Valid_Rows()
    {
        var admin = AdminClient("bulk-happy");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items/bulk-import", new
        {
            items = new object[]
            {
                new { name = $"Explosive-{suffix}", category = "weapons", severity = "block" },
                new { name = $"Fireworks-{suffix}", category = "weapons", severity = "warn" },
                new { name = $"Counterfeit-{suffix}", category = "fraud" }
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BulkResult>();
        body!.Imported.Should().Be(3);
        body.Skipped.Should().Be(0);
        body.Total.Should().Be(3);
        body.Results.Should().OnlyContain(r => r.Outcome == "created");
        body.Results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Id));
    }

    [Fact]
    public async Task BulkImport_Reports_Invalid_And_Duplicate_Rows_Without_Aborting()
    {
        var admin = AdminClient("bulk-partial");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dupName = $"Dup-{suffix}";

        // Seed a name that the batch will collide with.
        var seed = await admin.PostAsJsonAsync("/admin/prohibited-items",
            new { name = dupName, category = "weapons" });
        seed.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items/bulk-import", new
        {
            items = new object[]
            {
                new { name = $"Valid-{suffix}", category = "weapons" },      // index 0: created
                new { name = "", category = "weapons" },                      // index 1: invalid (blank name)
                new { name = $"BadCat-{suffix}", category = "Weapons!" },     // index 2: invalid (bad slug)
                new { name = dupName, category = "weapons" }                  // index 3: duplicate
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BulkResult>();
        body!.Total.Should().Be(4);
        body.Imported.Should().Be(1);
        body.Skipped.Should().Be(3);

        body.Results.Single(r => r.Index == 0).Outcome.Should().Be("created");
        body.Results.Single(r => r.Index == 1).Outcome.Should().Be("invalid");
        body.Results.Single(r => r.Index == 2).Outcome.Should().Be("invalid");
        body.Results.Single(r => r.Index == 3).Outcome.Should().Be("duplicate");
    }

    [Fact]
    public async Task BulkImport_With_Empty_Items_Returns_400()
    {
        var admin = AdminClient("bulk-empty");

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items/bulk-import",
            new { items = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkImport_Without_Admin_Role_Returns_403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "bulk-nonadmin");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.PostAsJsonAsync("/admin/prohibited-items/bulk-import", new
        {
            items = new[] { new { name = "X", category = "weapons" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BulkImport_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient(); // no identity headers

        var resp = await client.PostAsJsonAsync("/admin/prohibited-items/bulk-import", new
        {
            items = new[] { new { name = "X", category = "weapons" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private HttpClient AdminClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private sealed record BulkResult(
        int Imported,
        int Skipped,
        int Total,
        IReadOnlyList<RowResult> Results);

    private sealed record RowResult(int Index, string Outcome, string? Id, string? Name, string? Error);
}
