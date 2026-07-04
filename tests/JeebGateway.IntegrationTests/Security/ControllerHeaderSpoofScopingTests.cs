using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.Security;

/// <summary>
/// SEC-C1 (Leg-11) — closes the raw-<c>X-User-Id</c> spoof/IDOR class the security lane fixed
/// centrally in <see cref="JeebGateway.Users.UserIdentity"/> but that was left open in four
/// controllers (SavedLocations, NotificationPreferences, Realtime, CmsAuthoring).
///
/// Each test drives a production-like environment (a valid non-placeholder signing key satisfies
/// the SEC-H2 boot guard so it does not mask the C1 behaviour) with NO trusted-edge secret, then
/// sends a forged <c>X-User-Id</c> header and asserts the header is NOT honoured as identity: the
/// self-scoped endpoints refuse to resolve identity from it, and the CMS publish actor/audit id is
/// never the forged value.
/// </summary>
public sealed class ControllerHeaderSpoofScopingTests
{
    private const string ValidSigningKey = "spoof-scoping-test-signing-key-32bytes-min!!";
    private const string ForgedUserId = "11111111-2222-3333-4444-555555555555";

    private static WebApplicationFactory<Program> ProdFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Security:RateLimit:Enabled", "false");
            b.UseSetting("Security:TokenMint:Enabled", "false");
            b.UseSetting("Jwt:SigningKey", ValidSigningKey);
            // No Security:EdgeIdentity:SharedSecret → fail closed, raw X-User-* never trusted.
        });

    [Fact]
    public async Task SavedLocations_Forged_XUserId_Is_Not_Trusted_As_Identity()
    {
        using var factory = ProdFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.GetAsync("/api/users/me/saved-locations");

        resp.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
            "a raw client X-User-Id must not scope another user's saved locations in production");
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NotificationPreferences_Forged_XUserId_Is_Not_Trusted_As_Identity()
    {
        using var factory = ProdFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.GetAsync("/v1/notifications/preferences");

        resp.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
            "a raw client X-User-Id must not scope another user's notification preferences in production");
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Realtime_FanOut_Forged_XUserId_Is_Not_Trusted_As_Sender()
    {
        using var factory = ProdFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var body = new
        {
            recipientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            messageId = "m-1",
            type = "text",
            body = "hi",
        };
        var resp = await client.PostAsync(
            "/realtime/chat/fanout",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        // Identity is never resolved from the raw header, so the request is rejected at the
        // identity/authorization boundary and never fans out AS the forged sender.
        resp.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
            "a raw client X-User-Id must not let a caller fan out chat as another sender in production");
    }

    [Fact]
    public async Task Cms_Publish_Actor_Comes_From_Token_Not_The_Forged_XUserId_In_Production()
    {
        const string surface = "ofl-cms-orders-mfe";
        const string @base = "/gateway/admin/v1/cms";
        const string principalUserId = "aaaa1111-2222-3333-4444-555566667777"; // the REAL authenticated caller
        const string devStepUpCode = "424242";                                 // documented CMS mock TOTP

        using var factory = ProdFactory();
        var client = factory.CreateClient();
        // An authenticated caller (valid bearer) clears the CMS capability/TOTP gates, then tries to
        // forge the publish actor/audit id via a raw X-User-Id header pointing at ANOTHER user.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, principalUserId));
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-Step-Up-Totp", devStepUpCode);

        var resp = await client.PostAsync($"{@base}/config/{surface}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an authenticated caller passing the CMS capability/TOTP gates is admitted to publish");
        var publishBody = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var actor = publishBody.GetProperty("publishedByUserId").GetString();

        actor.Should().Be(principalUserId,
            "the publish actor/audit id must come from the validated principal (token sub/sid)");
        actor.Should().NotBe(ForgedUserId,
            "the raw X-User-Id header must never override the validated principal as the audit actor");
    }

    private static string MintGatewayBearer(WebApplicationFactory<Program> factory, string userId)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", userId),
            new(ClaimTypes.Sid, userId),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
