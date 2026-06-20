using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-01 — CMS authoring plane (W4). Pins the §4 step-up TOTP state machine and
/// the surface/version/publish flow:
///
/// <list type="bullet">
///   <item>Step-up gate: missing/malformed → 401 step_up_required;
///     wrong → 403 step_up_invalid; valid 424242 → 200 (version bump).</item>
///   <item>Capability gate runs first: X-Cms-Capability: deny → 403.</item>
///   <item>Gate ordering: unknown surface without TOTP → 401, NOT 404.</item>
///   <item>Surfaces list, draft upsert, versions, diff.</item>
/// </list>
/// </summary>
public sealed class CmsAuthoringEndpointTests
{
    private const string Surface = "ofl-cms-orders-mfe";
    private const string Base = "/gateway/admin/v1/cms";

    private static HttpClient Client(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", "cms-admin-1");
        return c;
    }

    private static async Task<string?> ProblemType(HttpResponseMessage resp)
    {
        var el = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return el.TryGetProperty("type", out var t) ? t.GetString() : null;
    }

    // ---- step-up gate -------------------------------------------------------

    [Fact]
    public async Task Publish_Without_StepUp_Header_Returns_401_StepUpRequired()
    {
        using var f = new WebApplicationFactory<Program>();
        var resp = await Client(f).PostAsync($"{Base}/config/{Surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:step_up_required");
    }

    [Theory]
    [InlineData("12345")]   // five digits
    [InlineData("1234567")] // seven digits
    [InlineData("abcdef")]  // non-numeric
    [InlineData("42 4242")] // space
    public async Task Publish_With_Malformed_Totp_Returns_401_StepUpRequired(string totp)
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);
        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", totp);

        var resp = await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:step_up_required");
    }

    [Fact]
    public async Task Publish_With_Wrong_Totp_Returns_403_StepUpInvalid()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);
        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "111111");

        var resp = await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:step_up_invalid");
    }

    [Fact]
    public async Task Publish_With_Valid_Totp_Returns_200_And_Bumps_Version()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);

        // Seeded surfaces start at published v1.
        var before = await http.GetFromJsonAsync<JsonElement>($"{Base}/config/{Surface}/published");
        before.GetProperty("version").GetInt32().Should().Be(1);

        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "424242");
        var resp = await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetInt32().Should().Be(2);
        body.GetProperty("surfaceId").GetString().Should().Be(Surface);
        body.GetProperty("publishedByUserId").GetString().Should().Be("cms-admin-1");
    }

    // ---- capability + ordering ---------------------------------------------

    [Fact]
    public async Task Publish_With_CapabilityDeny_Returns_403_Even_With_Valid_Totp()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);
        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "424242");
        http.DefaultRequestHeaders.Add("X-Cms-Capability", "deny");

        var resp = await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:forbidden");
    }

    [Fact]
    public async Task Publish_Unknown_Surface_Without_Totp_Returns_401_Not_404()
    {
        using var f = new WebApplicationFactory<Program>();
        var resp = await Client(f)
            .PostAsync($"{Base}/config/does-not-exist/publish", content: null);

        // Step-up runs before surface lookup → 401, not 404.
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:step_up_required");
    }

    [Fact]
    public async Task Publish_Unknown_Surface_With_Valid_Totp_Returns_404()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);
        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "424242");

        var resp = await http.PostAsync($"{Base}/config/does-not-exist/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemType(resp)).Should().Be("urn:jeeb:error:surface_not_found");
    }

    // ---- surfaces + draft + versions + diff --------------------------------

    [Fact]
    public async Task ListSurfaces_Returns_Four_Seeded_Surfaces()
    {
        using var f = new WebApplicationFactory<Program>();
        var body = await Client(f).GetFromJsonAsync<JsonElement>($"{Base}/surfaces");

        var ids = body.GetProperty("surfaces").EnumerateArray()
            .Select(s => s.GetProperty("surfaceId").GetString())
            .ToList();

        ids.Should().Contain(new[]
        {
            "ofl-cms-orders-mfe", "ofl-cms-users-mfe",
            "ofl-cms-wallet-mfe", "ofl-cms-kyc-mfe",
        });
    }

    [Fact]
    public async Task Draft_Upsert_Then_Read_Roundtrips_And_Sets_HasDraft()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);

        var put = await http.PutAsJsonAsync(
            $"{Base}/config/{Surface}/draft",
            new { config = new Dictionary<string, object?> { ["banner"] = "promo-x" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var draft = await http.GetFromJsonAsync<JsonElement>($"{Base}/config/{Surface}/draft");
        draft.GetProperty("config").GetProperty("banner").GetString().Should().Be("promo-x");
    }

    [Fact]
    public async Task Draft_Upsert_With_CapabilityDeny_Returns_403()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);
        http.DefaultRequestHeaders.Add("X-Cms-Capability", "deny");

        var put = await http.PutAsJsonAsync(
            $"{Base}/config/{Surface}/draft",
            new { config = new Dictionary<string, object?> { ["x"] = 1 } });

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemType(put)).Should().Be("urn:jeeb:error:forbidden");
    }

    [Fact]
    public async Task Versions_Lists_History_After_Publish()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);

        await http.PutAsJsonAsync(
            $"{Base}/config/{Surface}/draft",
            new { config = new Dictionary<string, object?> { ["enabled"] = false, ["banner"] = "v2" } });

        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "424242");
        var pub = await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);
        pub.StatusCode.Should().Be(HttpStatusCode.OK);

        var versions = await http.GetFromJsonAsync<JsonElement>($"{Base}/config/{Surface}/versions");
        versions.GetProperty("versions").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Diff_Reports_Changed_And_Added_Keys_Between_Versions()
    {
        using var f = new WebApplicationFactory<Program>();
        var http = Client(f);

        // v1 (seed): { surfaceId, enabled:true }. Draft adds "banner" and flips enabled.
        await http.PutAsJsonAsync(
            $"{Base}/config/{Surface}/draft",
            new { config = new Dictionary<string, object?> { ["surfaceId"] = Surface, ["enabled"] = false, ["banner"] = "promo" } });

        http.DefaultRequestHeaders.Add("X-Step-Up-Totp", "424242");
        await http.PostAsync($"{Base}/config/{Surface}/publish", content: null);

        var diff = await http.GetFromJsonAsync<JsonElement>($"{Base}/config/{Surface}/diff?from=1&to=2");
        diff.GetProperty("added").EnumerateArray().Select(x => x.GetString()).Should().Contain("banner");
        diff.GetProperty("changed").EnumerateArray()
            .Select(x => x.GetProperty("key").GetString()).Should().Contain("enabled");
    }

    [Fact]
    public async Task DevStepUpTotp_Returns_Documented_Code()
    {
        using var f = new WebApplicationFactory<Program>();
        var body = await Client(f).GetFromJsonAsync<JsonElement>($"{Base}/dev/step-up-totp");

        body.GetProperty("code").GetString().Should().Be("424242");
        body.GetProperty("expiresInSeconds").GetInt32().Should().Be(900);
    }
}
