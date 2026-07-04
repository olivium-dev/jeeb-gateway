using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Security;

/// <summary>
/// SEC-IDOR (Leg-11) — DELETE /api/User/profile is the SELF profile-delete (ProfileWriteSelf =
/// any-authenticated). Before the fix, the optional ?userId=X query param won over the caller's
/// token id with NO ownership check, so any authenticated caller could permanently delete ANY
/// account (BOLA / OWASP API1). The guard enforces caller == target (admins excepted).
/// </summary>
public class AccountDeleteIdorTests
{
    private const string OtherUserId = "deadbeef-0000-1111-2222-333344445555";

    [Fact]
    public async Task Delete_Other_Users_Account_Is_Forbidden()
    {
        using var factory = new WebApplicationFactory<Program>();
        var bearer = CapabilityTestHarness.MintBearer(factory, "client"); // caller = CapabilityTestHarness.UserId
        var client = factory.CreateClient().WithBearer(bearer);

        var resp = await client.DeleteAsync($"/api/User/profile?userId={OtherUserId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an authenticated caller must not be able to delete an account that is not their own");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/forbidden-ownership");
        problem.GetProperty("status").GetInt32().Should().Be(403);
    }

    [Fact]
    public async Task Delete_Own_Account_Passes_The_Ownership_Guard()
    {
        // Control: deleting one's OWN id (same as the token subject) must NOT be blocked by the
        // ownership guard. It proceeds past the guard to the upstream call (which is absent in the
        // test host), so the result is any non-authorization status — the point is it is NOT 403/401.
        using var factory = new WebApplicationFactory<Program>();
        var bearer = CapabilityTestHarness.MintBearer(factory, "client");
        var client = factory.CreateClient().WithBearer(bearer);

        var resp = await client.DeleteAsync($"/api/User/profile?userId={CapabilityTestHarness.UserId}");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "the legitimate owner must pass the ownership guard");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
